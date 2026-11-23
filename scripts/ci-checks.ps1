<#
.SYNOPSIS
    Repo checks to run locally before a push (the deploy runs format, build, Unit, Integration and ruff).

.DESCRIPTION
    Run from the repo root after `dotnet build PoLocalCompare.slnx -c Release`. deploy.yml runs
    formatting, ruff, the build and the Unit and Integration tiers; these checks are the local
    gate on top (and the LFS step the deploy needs lives in scripts/ci-package.ps1).

    0. Git LFS. src/PoLocalCompare.Client/wwwroot/js/web-llm.js is an LFS object; a clone without
       LFS has the ~130-byte pointer. Pull the real file and rebuild the API (which carries the
       client's static assets). Without it every browser model fails at import time.
    1. Repo hygiene: scripts/check-clean.ps1 (test budgets, dead/unused CSS, cache-busters, motion gate).
    2. Browser-model catalog: scripts/plan-webllm-artifacts.py exits non-zero when ModelSeeder.cs
       and web-llm.js's prebuiltAppConfig disagree. Needs the LFS file from step 0.
    3. Python lint and format: ruff check + ruff format --check (ruff.toml) over scripts/*.py, through uvx.
    4. Integration and E2EAPI tiers. Both spin Azurite through Testcontainers, so they need Docker;
       without it they skip with one message. E2EUI stays local: it needs a running app and Chromium.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Host "::group::$Name"
    & $Command
    $code = $LASTEXITCODE
    Write-Host '::endgroup::'
    if ($code -ne 0) { Write-Host "::error::$Name failed (exit $code)"; exit $code }
}

$webLlm = 'src/PoLocalCompare.Client/wwwroot/js/web-llm.js'
if ((Get-Content $webLlm -TotalCount 1) -like 'version https://git-lfs*') {
    Invoke-Step 'Fetch Git LFS objects' { git lfs pull }
    if ((Get-Content $webLlm -TotalCount 1) -like 'version https://git-lfs*') {
        Write-Host "::error::$webLlm is still an LFS pointer after git lfs pull."
        exit 1
    }
    Invoke-Step 'Rebuild the API with the real web-llm.js' {
        dotnet build src/PoLocalCompare.Api/PoLocalCompare.Api.csproj -c Release --no-restore
    }
}

Invoke-Step 'Repo hygiene (check-clean)' { pwsh -NoProfile -File ./scripts/check-clean.ps1 }

$python = if ($IsWindows) {
    'python'
}
elseif (Get-Command python3 -ErrorAction SilentlyContinue) {
    'python3'
}
else {
    'python'
}
Invoke-Step 'Browser-model catalog agrees' { & $python scripts/plan-webllm-artifacts.py }

Invoke-Step 'Python lint (ruff)' { uvx ruff check . }
Invoke-Step 'Python format (ruff)' { uvx ruff format --check . }

foreach ($suite in 'Integration', 'E2EAPI') {
    Invoke-Step "$suite tests" { dotnet test "tests/PoLocalCompare.$suite" -c Release --no-build --logger "trx;LogFileName=$suite.trx" }
}

Write-Host 'ci-checks: all passed.'
