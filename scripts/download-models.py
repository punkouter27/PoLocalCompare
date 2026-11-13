"""
Download WebLLM MLC model weights + model libraries directly from the internet.

Run from repo root on a machine with egress to huggingface.co and
raw.githubusercontent.com:

    python SCRIPTS/download-models.py            # every seeded model
    MODELS=small python SCRIPTS/download-models.py
    MODELS=Qwen3-1.7B-q4f16_1-MLC python SCRIPTS/download-models.py

The model list is derived from ModelSeeder.cs + web-llm.js by
plan-webllm-artifacts.py, so there is no second list to keep in sync. MODELS is
passed straight through to the planner and accepts the same values.

Weights land in src/PoLocalCompare.Client/wwwroot/models/{webLlmId}/ and
libraries in .../models/_libs/, served as static files from
https://localhost:5001/models/.

Why curl.exe instead of huggingface_hub: on this network TLS handshakes to
huggingface.co are reset at random -- schannel cannot reach the certificate
revocation responder (CRYPT_E_REVOCATION_OFFLINE), and something upstream resets
roughly half of new connections regardless. Python's own TLS stack fails the same
way and huggingface_hub gives up immediately, so we shell out to curl with
aggressive retries and byte-range resume. Established connections run at full
speed; it is only connection setup that is unreliable.

Integrity does not depend on that: every LFS file is verified against the sha256
that the Hub advertises in its tree listing, and a file that fails is re-fetched.

There is no air-gapped route in this repo. Older revisions of AGENT.MD and
SCRIPTS/README.md pointed at a "Fetch WebLLM artifacts" GitHub Actions
workflow plus receive-artifacts.ps1; neither has ever existed here
(.github/workflows/ holds only deploy.yml). If this machine cannot reach
huggingface.co, browser models fall back to the CDN or fail to load.
"""

import hashlib
import json
import os
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE_DIR = ROOT / "src" / "Client" / "PoLocalCompare.Client" / "wwwroot" / "models"
LIB_DIR = BASE_DIR / "_libs"

IGNORE = {".gitattributes", "README.md", "ndarray-cache-b16.json"}

# Connection setup is the unreliable part, so retry hard and resume in place.
CURL = [
    "curl.exe", "-sSL", "--ssl-no-revoke",
    "--retry", "20", "--retry-all-errors", "--retry-delay", "1",
    "--connect-timeout", "20",
]
PARALLEL = 4


def curl_bytes(url: str) -> bytes:
    proc = subprocess.run(CURL + ["--max-time", "120", url], capture_output=True)
    if proc.returncode != 0:
        raise RuntimeError(f"curl {url} failed: {proc.stderr.decode(errors='replace').strip()}")
    return proc.stdout


def curl_file(url: str, dest: Path) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    proc = subprocess.run(CURL + ["-C", "-", "-o", str(dest), url], capture_output=True)
    if proc.returncode != 0:
        raise RuntimeError(f"curl {url} failed: {proc.stderr.decode(errors='replace').strip()}")


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        while chunk := fh.read(1 << 20):
            h.update(chunk)
    return h.hexdigest()


def plan() -> tuple[list[dict], str]:
    """Ask plan-webllm-artifacts.py which models and libraries are in scope."""
    proc = subprocess.run(
        [sys.executable, str(Path(__file__).with_name("plan-webllm-artifacts.py"))],
        cwd=str(ROOT), capture_output=True, text=True,
        env={**os.environ, "GITHUB_OUTPUT": ""},
    )
    sys.stderr.write(proc.stderr)
    if proc.returncode != 0:
        sys.exit(proc.returncode)
    entries = lib_base = None
    for line in proc.stdout.splitlines():
        if line.startswith("matrix="):
            entries = json.loads(line[len("matrix="):])
        elif line.startswith("lib_base="):
            lib_base = line[len("lib_base="):]
    if entries is None or lib_base is None:
        sys.exit("planner did not emit matrix/lib_base")
    return entries, lib_base


def list_repo_files(repo: str) -> list[dict]:
    tree = json.loads(curl_bytes(f"https://huggingface.co/api/models/{repo}/tree/main?recursive=1"))
    files = []
    for e in tree:
        if e.get("type") != "file" or Path(e["path"]).name in IGNORE:
            continue
        files.append({
            "path": e["path"],
            "size": e.get("size", 0),
            "sha": (e.get("lfs") or {}).get("oid"),
        })
    if not files:
        raise RuntimeError(f"no files listed for {repo}")
    return files


def up_to_date(dest: Path, meta: dict) -> bool:
    if not dest.exists():
        return False
    if meta["sha"]:
        return sha256(dest) == meta["sha"]
    return dest.stat().st_size == meta["size"]


def fetch_one(repo: str, local_dir: Path, meta: dict) -> str:
    dest = local_dir / meta["path"]
    if up_to_date(dest, meta):
        return f"    skip  {meta['path']}"
    url = f"https://huggingface.co/{repo}/resolve/main/{meta['path']}"
    curl_file(url, dest)
    if meta["sha"] and sha256(dest) != meta["sha"]:
        # A reset mid-transfer can leave a resumed file inconsistent; one clean retry.
        dest.unlink()
        curl_file(url, dest)
        if sha256(dest) != meta["sha"]:
            raise RuntimeError(f"sha256 mismatch after retry: {meta['path']}")
    return f"    got   {meta['path']} ({dest.stat().st_size / 1048576:.1f} MB)"


def download_weights(entry: dict) -> float:
    local_dir = BASE_DIR / entry["dir"]
    local_dir.mkdir(parents=True, exist_ok=True)
    files = list_repo_files(entry["repo"])
    print(f"    {len(files)} files, {sum(f['size'] for f in files) / 1048576:.0f} MB")
    with ThreadPoolExecutor(max_workers=PARALLEL) as pool:
        for line in pool.map(lambda m: fetch_one(entry["repo"], local_dir, m), files):
            print(line)
    return sum(f.stat().st_size for f in local_dir.rglob("*") if f.is_file()) / 1048576


def download_lib(entry: dict, lib_base: str) -> float:
    dest = LIB_DIR / entry["lib"]
    if not (dest.exists() and dest.stat().st_size > 0):
        curl_file(f"{lib_base}/{entry['lib']}", dest)
    return dest.stat().st_size / 1048576


def main() -> None:
    entries, lib_base = plan()
    print(f"Model output directory: {BASE_DIR}")
    failed = []
    for entry in entries:
        print(f"\n{'=' * 60}\n  {entry['dir']}\n{'=' * 60}")
        try:
            lib_mb = download_lib(entry, lib_base)
            weights_mb = download_weights(entry)
            print(f"  Done - {weights_mb:.0f} MB weights, {lib_mb:.0f} MB library")
        except Exception as exc:  # noqa: BLE001 - report every failure, fail at the end
            print(f"ERROR {entry['dir']}: {exc}", file=sys.stderr)
            failed.append(entry["dir"])
    if failed:
        sys.exit("\nFailed: " + ", ".join(failed))
    print(f"\nAll {len(entries)} models downloaded to: {BASE_DIR}")


if __name__ == "__main__":
    main()
