<#
.SYNOPSIS
    Repo-specific build checks, run by the shared deploy.yml after the unit tests.

.DESCRIPTION
    Runs from the repo root on ubuntu-latest, after `dotnet build *.slnx -c Release`.

    0. Git LFS. src/PoLocalCompare.Client/wwwroot/js/web-llm.js is an LFS object and the shared
       checkout does not fetch LFS, so the build saw the ~130-byte pointer. Pull the real file and
       rebuild the API (which carries the client's static assets) so the `--no-build` publish that
       follows ships the real bundle. Without it every browser model fails at import time.
    1. Repo hygiene: scripts/check-clean.ps1 (test budgets, dead/unused CSS, cache-busters, motion gate).
    2. Browser-model catalog: scripts/plan-webllm-artifacts.py exits non-zero when ModelSeeder.cs
       and web-llm.js's prebuiltAppConfig disagree. Needs the LFS file from step 0.
    3. Integration and E2EAPI tiers. Both spin Azurite through Testcontainers, so they need Docker
       (ubuntu-latest has a daemon). E2EUI stays local: it needs a headed Chrome with WebGPU.
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

foreach ($suite in 'Integration', 'E2EAPI') {
    Invoke-Step "$suite tests" { dotnet test "tests/PoLocalCompare.$suite" -c Release --no-build --logger "trx;LogFileName=$suite.trx" }
}

Write-Host 'ci-checks: all passed.'
