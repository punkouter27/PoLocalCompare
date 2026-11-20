<#
.SYNOPSIS
    Deploy hook: makes sure the package ships the real web-llm.js, not its Git LFS pointer.

.DESCRIPTION
    Called by deploy.yml after `dotnet publish` (step "Package extras"), from the repo
    root on ubuntu-latest. src/PoLocalCompare.Client/wwwroot/js/web-llm.js is a Git LFS object and
    the checkout does not fetch LFS, so the build and the publish both saw the ~130-byte
    pointer; every browser model would then fail at import time. When that is the case this pulls
    the LFS objects and publishes again into the same folder, so the static-asset manifest,
    compressed copies and fingerprints are all rebuilt from the real file.

    scripts/ci-checks.ps1 has the same LFS step for local runs.

.EXAMPLE
    pwsh ./scripts/ci-package.ps1 -PublishDir publish
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDir)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$webLlm = 'src/PoLocalCompare.Client/wwwroot/js/web-llm.js'
if ((Get-Content $webLlm -TotalCount 1) -notlike 'version https://git-lfs*') {
    Write-Host "$webLlm is the real bundle; nothing to do."
    exit 0
}

git lfs pull
if ($LASTEXITCODE -ne 0) { Write-Host '::error::git lfs pull failed.'; exit $LASTEXITCODE }
if ((Get-Content $webLlm -TotalCount 1) -like 'version https://git-lfs*') {
    Write-Host "::error::$webLlm is still an LFS pointer after git lfs pull."
    exit 1
}

dotnet publish src/PoLocalCompare.Api/PoLocalCompare.Api.csproj -c Release -o $PublishDir
exit $LASTEXITCODE
