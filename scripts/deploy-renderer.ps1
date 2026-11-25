#Requires -Version 7
<#
.SYNOPSIS
    Builds PoLocalCompare.RenderService, pushes it to GHCR and deploys it to Azure Container Apps.

.DESCRIPTION
    The vision judge needs headless Chromium, which the Free (F1) App Service cannot run, so the
    Api posts each page to this container instead (Rendering:ServiceUrl). Re-runnable; run it again
    after changing src/PoLocalCompare.Rendering or src/PoLocalCompare.RenderService.

      1. docker build (src/PoLocalCompare.RenderService/Dockerfile), tagged with the HEAD commit.
      2. docker push to ghcr.io with the gh CLI's token (needs the write:packages scope).
         The package must be PUBLIC so Container Apps can pull it without a registry secret;
         the script stops with the settings link if it is not.
      3. Reads PoLocalCompare--Rendering--ApiKey from Key Vault, creating it on first run.
      4. Deploys infra/modules/renderer.bicep into the app's resource group (Container Apps
         environment cae-polocalcompare + app ca-polocalcompare-render, scale-to-zero).
      5. Waits for the service's /health, then writes its URL to Rendering:ServiceUrl and turns
         AiJudge:VisionEnabled on in src/PoLocalCompare.Api/appsettings.json.

    Then commit appsettings.json and push: deploy.yml ships the Api, which reads the key from the
    vault at startup. Run from the repo root:
        pwsh scripts/deploy-renderer.ps1
#>

[CmdletBinding()]
param (
    [string]$ResourceGroup = 'PoLocalCompare',
    [string]$VaultName = 'kv-poshared2',
    [string]$LogAnalyticsWorkspace = 'PoShared-LogAnalytics',
    [string]$LogAnalyticsResourceGroup = 'PoShared',
    [string]$GitHubOwner = 'punkouter27',
    [string]$PackageName = 'polocalcompare-render',
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$SecretName = 'PoLocalCompare--Rendering--ApiKey'

function Write-Step([string]$Msg) { Write-Host "`n==> $Msg" -ForegroundColor Cyan }
function Write-Ok([string]$Msg)   { Write-Host "    [OK] $Msg" -ForegroundColor Green }

# Fails on a non-zero exit, naming only the subcommand: some calls carry the key.
function Invoke-Native([string]$Exe) {
    $out = & $Exe @args
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($args[0]) $($args[1]) failed (exit $LASTEXITCODE)" }
    $out
}

Push-Location $RepoRoot
try {
    $tag = (Invoke-Native git rev-parse --short=12 HEAD).Trim()
    $image = "ghcr.io/$GitHubOwner/${PackageName}:$tag"

    # ─── 1-2. Image ──────────────────────────────────────────────────────────
    if (-not $SkipBuild) {
        Write-Step "Building $image"
        Invoke-Native docker build -f src/PoLocalCompare.RenderService/Dockerfile `
            --label "org.opencontainers.image.source=https://github.com/$GitHubOwner/PoLocalCompare" `
            -t $image . | Out-Host
        Write-Ok "Built"

        Write-Step "Pushing to ghcr.io"
        $token = Invoke-Native gh auth token
        $token | docker login ghcr.io -u $GitHubOwner --password-stdin | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'docker login ghcr.io failed.' }
        & docker push $image | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "docker push failed. If it was denied, add the scope and re-run: gh auth refresh -s write:packages,read:packages"
        }
        Write-Ok "Pushed $image"
    }

    $visibility = & gh api "/user/packages/container/$PackageName" --jq .visibility 2>$null
    if ($visibility -ne 'public') {
        throw ("ghcr.io/$GitHubOwner/$PackageName is '$visibility', and Container Apps pulls it anonymously. " +
            "Make it public at https://github.com/users/$GitHubOwner/packages/container/$PackageName/settings " +
            "(Danger Zone > Change visibility), then re-run with -SkipBuild.")
    }
    Write-Ok "Package is public"

    # ─── 3. Key ──────────────────────────────────────────────────────────────
    Write-Step "Render key ($SecretName in $VaultName)"
    $key = & az keyvault secret show --vault-name $VaultName --name $SecretName --query value -o tsv 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $key) {
        $key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        Invoke-Native az keyvault secret set --vault-name $VaultName --name $SecretName --value $key -o none
        Write-Ok "Created"
    } else {
        Write-Ok "Exists"
    }

    # ─── 4. Infrastructure ───────────────────────────────────────────────────
    Write-Step "Registering resource providers"
    foreach ($ns in 'Microsoft.App', 'Microsoft.OperationalInsights') {
        Invoke-Native az provider register --namespace $ns --wait -o none
        Write-Ok $ns
    }

    Write-Step "Deploying infra/modules/renderer.bicep to $ResourceGroup"
    # Parameters through a file so the key never appears on a command line.
    $paramFile = New-TemporaryFile
    try {
        @{
            '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
            contentVersion = '1.0.0.0'
            parameters     = @{
                image                     = @{ value = $image }
                renderApiKey              = @{ value = $key }
                logAnalyticsWorkspaceName = @{ value = $LogAnalyticsWorkspace }
                logAnalyticsResourceGroup = @{ value = $LogAnalyticsResourceGroup }
            }
        } | ConvertTo-Json -Depth 5 | Set-Content $paramFile
        $url = Invoke-Native az deployment group create `
            --resource-group $ResourceGroup `
            --name "polocalcompare-render-$(Get-Date -Format yyyyMMddHHmmss)" `
            --template-file infra/modules/renderer.bicep `
            --parameters "@$paramFile" `
            --query properties.outputs.renderServiceUrl.value -o tsv
    } finally {
        Remove-Item $paramFile
        $key = $null
    }
    $url = $url.Trim()
    Write-Ok $url

    # ─── 5. Smoke test + app config ──────────────────────────────────────────
    Write-Step "Waiting for $url/health (a cold start pulls the image and launches Chromium)"
    $healthy = $false
    for ($i = 0; $i -lt 30 -and -not $healthy; $i++) {
        try { $healthy = (Invoke-WebRequest "$url/health" -TimeoutSec 20 -SkipHttpErrorCheck).StatusCode -eq 200 } catch { }
        if (-not $healthy) { Start-Sleep -Seconds 5 }
    }
    if (-not $healthy) { throw "$url/health did not answer 200 within ~3 minutes. Check: az containerapp logs show -g $ResourceGroup -n ca-polocalcompare-render" }
    Write-Ok "Healthy"

    Write-Step "Pointing appsettings.json at the service"
    $path = Join-Path $RepoRoot 'src/PoLocalCompare.Api/appsettings.json'
    $text = Get-Content $path -Raw
    $updated = $text -replace '("Rendering":\s*\{[^}]*?"ServiceUrl":\s*)"[^"]*"', "`$1`"$url`""
    $updated = $updated -replace '("AiJudge":\s*\{[^}]*?"VisionEnabled":\s*)false', '$1true'
    if ($updated -ne $text) { [IO.File]::WriteAllText($path, $updated); Write-Ok 'Rendering:ServiceUrl set, AiJudge:VisionEnabled = true' }
    else { Write-Ok 'Already set' }

    Write-Host "`n==> Render service deployed." -ForegroundColor Green
    Write-Host "    Next: commit src/PoLocalCompare.Api/appsettings.json and push to master." -ForegroundColor White
}
finally {
    Pop-Location
}
