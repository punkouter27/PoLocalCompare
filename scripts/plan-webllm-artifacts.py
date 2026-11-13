import json, os, re, sys

SEEDER = "src/PoLocalCompare.Api/Features/Models/ModelSeeder.cs"
WEBLLM = "src/PoLocalCompare.Client/wwwroot/js/web-llm.js"

seeder = open(SEEDER, encoding="utf-8").read()
ids = re.findall(r'webLlmModelId:\s*"([^"]+)"', seeder)
if not ids:
    sys.exit(f"no webLlmModelId entries found in {SEEDER}")

src = open(WEBLLM, encoding="utf-8").read()
lines = src.split("\n")
m = re.search(r'const modelVersion = "([^"]+)"', src)
if not m:
    sys.exit("modelVersion not found in web-llm.js")
model_version = m.group(1)
prefix = re.search(r'const modelLibURLPrefix = "([^"]+)"', src).group(1).rstrip("/")

entries, missing = [], []
for mid in ids:
    hits = [i for i, l in enumerate(lines) if f'model_id: "{mid}"' in l]
    if not hits:
        missing.append(mid)
        continue
    window = "\n".join(lines[max(0, hits[0] - 4):hits[0] + 12])
    repo = re.search(r'model:\s*"https://huggingface\.co/([^"]+)"', window)
    lib = re.search(r'model_lib:\s*modelLibURLPrefix\s*\+\s*modelVersion\s*\+\s*"/?([^"]+)"', window, re.S)
    if not repo or not lib:
        missing.append(mid)
        continue
    entries.append({"dir": mid, "repo": repo.group(1), "lib": lib.group(1)})

if missing:
    sys.exit("no prebuiltAppConfig entry for: " + ", ".join(missing))

wanted = (os.environ.get("MODELS") or "all").strip()
if wanted and wanted != "all":
    if wanted == "small":
        keep = {"SmolLM2-360M-Instruct-q4f32_1-MLC", "Qwen2.5-0.5B-Instruct-q4f32_1-MLC"}
    else:
        keep = {w.strip() for w in wanted.split(",") if w.strip()}
    unknown = keep - {e["dir"] for e in entries}
    if unknown:
        sys.exit("unknown model id(s): " + ", ".join(sorted(unknown)))
    entries = [e for e in entries if e["dir"] in keep]

print(f"modelVersion={model_version}", file=sys.stderr)
for e in entries:
    print(f"  {e['dir']:<38} {e['repo']:<48} {e['lib']}", file=sys.stderr)

out = os.environ.get("GITHUB_OUTPUT")
payload = [
    f"matrix={json.dumps(entries, separators=(',', ':'))}",
    f"lib_base={prefix}/{model_version}",
]
if out:
    with open(out, "a", encoding="utf-8") as fh:
        fh.write("\n".join(payload) + "\n")
else:
    print("\n".join(payload))
