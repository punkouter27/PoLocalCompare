#Requires -Version 7
<#
.SYNOPSIS
    Provisions PoLocalCompare into an Azure subscription and wires GitHub Actions to deploy to it.

.DESCRIPTION
    Re-runnable end to end; every step converges rather than duplicates.

      1. Finds the target (punkouter27@gmail.com) and source (punkouter26@outlook.com) subscriptions
         in the Azure CLI, prompting 'az login' for whichever is missing. The CLI keeps both
         accounts signed in at once; this script switches between them with 'az account set'.
      2. Target: creates the GitHub Actions app registration, its service principal and a
         federated credential for master, then deploys infra/main.bicep at subscription scope.
      3. Source: reads every PoLocalCompare--* secret from the old kv-poshared (except
         ConnectionStrings, which point at the old storage account) and adds the new host's
         redirect URIs to the sign-in app registration.
      4. Target: writes those secrets to the new vault and restarts the app so it reads them.
      5. Sets AZURE_CLIENT_ID / AZURE_TENANT_ID / AZURE_SUBSCRIPTION_ID / AZURE_WEBAPP_NAME on the
         GitHub repo, and repoints KeyVault:Uri in the two appsettings files at the new vault.

    Run from the repo root:
        pwsh SCRIPTS/provision-azure.ps1

    Then commit the appsettings change and push; deploy.yml ships the code.
#>

[CmdletBinding()]
param (
    [string]$TargetAccount = 'punkouter27@gmail.com',
    [string]$TargetSubscriptionId,
    [string]$SourceAccount = 'punkouter26@outlook.com',
    [string]$SourceSubscriptionId,
    [string]$SourceVaultName = 'kv-poshared',
    [string]$Location = 'westus2',
    [string]$GitHubRepo = 'punkouter27/PoLocalCompare',
    [switch]$SkipSecretCopy,
    [switch]$SkipGitHub
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$AppResourceGroup = 'PoLocalCompare'
$CiAppName = 'PoLocalCompare-GitHub'
$OldHost = 'app-polocalcompare-win.azurewebsites.net'

function Write-Step([string]$Msg) { Write-Host "`n==> $Msg" -ForegroundColor Cyan }
function Write-Ok([string]$Msg)   { Write-Host "    [OK] $Msg" -ForegroundColor Green }
function Write-Warn([string]$Msg) { Write-Host "    [WARN] $Msg" -ForegroundColor Yellow }

# Runs az and fails on a non-zero exit. The error names only the subcommand, never the arguments,
# because some calls carry secret values.
function Invoke-Az {
    $out = & az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args[0]) $($args[1]) failed (exit $LASTEXITCODE)" }
    $out
}

function Resolve-Subscription([string]$Account, [string]$SubscriptionId) {
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $subs = @(Invoke-Az account list --all -o json | ConvertFrom-Json |
            Where-Object { $_.user.name -eq $Account -and $_.state -eq 'Enabled' })
        if ($SubscriptionId) { $subs = @($subs | Where-Object { $_.id -eq $SubscriptionId }) }
        if ($subs.Count -eq 1) { return $subs[0] }
        if ($subs.Count -gt 1) {
            $list = ($subs | ForEach-Object { "$($_.name) ($($_.id))" }) -join ', '
            throw "$Account has several subscriptions: $list. Re-run with the matching -*SubscriptionId."
        }
        if ($attempt -eq 0) {
            Write-Warn "No enabled subscription for $Account in the Azure CLI. Sign in as $Account in the browser window that opens (choose 'Use another account' if it offers a different one)."
            Invoke-Az login --only-show-errors -o none | Out-Null
        }
    }
    throw "Still no enabled subscription for $Account. Create one at https://portal.azure.com, then re-run."
}

# ─── 1. Subscriptions ─────────────────────────────────────────────────────────
Write-Step "Resolving subscriptions"
$target = Resolve-Subscription $TargetAccount $TargetSubscriptionId
Write-Ok "Target: $($target.name) ($($target.id)) as $TargetAccount"
$source = $null
if (-not $SkipSecretCopy) {
    $source = Resolve-Subscription $SourceAccount $SourceSubscriptionId
    Write-Ok "Source: $($source.name) ($($source.id)) as $SourceAccount"
}

# ─── 2. Target: CI identity + infrastructure ─────────────────────────────────
Invoke-Az account set --subscription $target.id

Write-Step "GitHub Actions identity ($CiAppName)"
$ciAppId = Invoke-Az ad app list --display-name $CiAppName --query '[0].appId' -o tsv
if (-not $ciAppId) {
    $ciAppId = Invoke-Az ad app create --display-name $CiAppName --query appId -o tsv
    Write-Ok "Created app registration $ciAppId"
} else {
    Write-Ok "App registration exists ($ciAppId)"
}
$ciSpId = Invoke-Az ad sp list --filter "appId eq '$ciAppId'" --query '[0].id' -o tsv
if (-not $ciSpId) {
    $ciSpId = Invoke-Az ad sp create --id $ciAppId --query id -o tsv
    Write-Ok "Created service principal"
}
$subject = "repo:${GitHubRepo}:ref:refs/heads/master"
$hasCredential = Invoke-Az ad app federated-credential list --id $ciAppId --query "[?subject=='$subject'].name" -o tsv
if (-not $hasCredential) {
    $credentialFile = New-TemporaryFile
    @{
        name      = 'github-master'
        issuer    = 'https://token.actions.githubusercontent.com'
        subject   = $subject
        audiences = @('api://AzureADTokenExchange')
    } | ConvertTo-Json | Set-Content $credentialFile
    try { Invoke-Az ad app federated-credential create --id $ciAppId --parameters "@$credentialFile" -o none }
    finally { Remove-Item $credentialFile }
    Write-Ok "Federated credential for $subject"
} else {
    Write-Ok "Federated credential exists"
}

Write-Step "Deploying infra/main.bicep to $Location"
$deployerId = Invoke-Az ad signed-in-user show --query id -o tsv
$outputs = Invoke-Az deployment sub create `
    --location $Location `
    --name "polocalcompare-$(Get-Date -Format yyyyMMddHHmmss)" `
    --template-file (Join-Path $RepoRoot 'infra/main.bicep') `
    --parameters deployerObjectId=$deployerId ciPrincipalId=$ciSpId `
    --query properties.outputs -o json | ConvertFrom-Json
$appName = $outputs.appServiceName.value
$appHost = "$appName.azurewebsites.net"
$vaultName = $outputs.keyVaultName.value
$vaultUri = $outputs.keyVaultUri.value
Write-Ok "App Service $appName, vault $vaultName, storage $($outputs.storageAccountName.value)"

# ─── 3. Source: secrets + sign-in redirect URIs ──────────────────────────────
$secrets = [ordered]@{}
if (-not $SkipSecretCopy) {
    Invoke-Az account set --subscription $source.id

    Write-Step "Reading PoLocalCompare--* secrets from $SourceVaultName"
    $names = @(Invoke-Az keyvault secret list --vault-name $SourceVaultName `
        --query "[?starts_with(name, 'PoLocalCompare--')].name" -o tsv | Where-Object { $_ })
    foreach ($name in $names) {
        # The old connection strings point at the outlook account's storage. Production uses
        # AzureStorage__AccountName, and Program.cs already refuses to let a vault connection
        # string repoint a local run, so they would only ever mislead.
        if ($name -like 'PoLocalCompare--ConnectionStrings--*') { Write-Warn "Skipping $name"; continue }
        $secrets[$name] = Invoke-Az keyvault secret show --vault-name $SourceVaultName --name $name --query value -o tsv
        Write-Ok $name
    }
}

# The sign-in app registration lives in whichever tenant created it; try each account we have.
Write-Step "Adding $appHost redirect URIs to the sign-in app registration"
$signInClientId = (Get-Content (Join-Path $RepoRoot 'src/PoLocalCompare.Api/appsettings.json') -Raw |
    ConvertFrom-Json).AzureAd.ClientId
$redirectDone = $false
foreach ($sub in (@($source, $target) | Where-Object { $_ })) {
    Invoke-Az account set --subscription $sub.id
    $web = az ad app show --id $signInClientId --query web -o json 2>$null | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $web) { continue }
    $existing = @($web.redirectUris | Where-Object { $_ })
    $wanted = @($existing | Where-Object { $_ -like "*$OldHost*" } | ForEach-Object { $_.Replace($OldHost, $appHost) })
    if (-not $wanted) { $wanted = @("https://$appHost/signin-oidc", "https://$appHost/signout-callback-oidc") }
    $merged = @($existing + $wanted | Select-Object -Unique)
    if ($merged.Count -gt $existing.Count) {
        Invoke-Az ad app update --id $signInClientId --web-redirect-uris @merged -o none
        Write-Ok "Added: $(($wanted | Where-Object { $_ -notin $existing }) -join ', ')"
    } else {
        Write-Ok "Redirect URIs already present"
    }
    $redirectDone = $true
    break
}
if (-not $redirectDone) {
    Write-Warn "Could not read app registration $signInClientId from either account. Add https://$appHost/signin-oidc to it by hand or sign-in will fail."
}

# ─── 4. Target: write secrets, restart ───────────────────────────────────────
Invoke-Az account set --subscription $target.id
if ($secrets.Count) {
    Write-Step "Writing $($secrets.Count) secret(s) to $vaultName"
    foreach ($name in $secrets.Keys) {
        # The Secrets Officer grant from main.bicep can take a minute or two to propagate.
        for ($try = 1; ; $try++) {
            & az keyvault secret set --vault-name $vaultName --name $name --value $secrets[$name] -o none 2>$null
            if ($LASTEXITCODE -eq 0) { break }
            if ($try -ge 12) { throw "Could not write $name to $vaultName after $try attempts." }
            Start-Sleep -Seconds 10
        }
        Write-Ok $name
    }
}
$secrets.Clear()

Write-Step "Restarting $appName so it reloads Key Vault"
Invoke-Az webapp restart --name $appName --resource-group $AppResourceGroup -o none
Write-Ok "Restarted"

# ─── 5. GitHub variables + local config ──────────────────────────────────────
if (-not $SkipGitHub) {
    Write-Step "Setting GitHub Actions variables on $GitHubRepo"
    & gh auth status *> $null
    if ($LASTEXITCODE -ne 0) { & gh auth login --hostname github.com --web }
    $vars = [ordered]@{
        AZURE_CLIENT_ID       = $ciAppId
        AZURE_TENANT_ID       = $target.tenantId
        AZURE_SUBSCRIPTION_ID = $target.id
        AZURE_WEBAPP_NAME     = $appName
    }
    foreach ($key in $vars.Keys) {
        & gh variable set $key --repo $GitHubRepo --body $vars[$key]
        if ($LASTEXITCODE -ne 0) { throw "gh variable set $key failed" }
        Write-Ok "$key = $($vars[$key])"
    }
}

Write-Step "Pointing local KeyVault:Uri at $vaultUri"
foreach ($file in 'appsettings.json', 'appsettings.Development.json') {
    $path = Join-Path $RepoRoot "src/PoLocalCompare.Api/$file"
    $text = Get-Content $path -Raw
    $updated = $text -replace '"Uri":\s*"https://[a-z0-9-]+\.vault\.azure\.net/?"', "`"Uri`": `"$vaultUri`""
    if ($updated -ne $text) { [IO.File]::WriteAllText($path, $updated); Write-Ok $file }
}
$readme = Join-Path $RepoRoot 'README.md'
$text = Get-Content $readme -Raw
if ($text.Contains($OldHost)) { [IO.File]::WriteAllText($readme, $text.Replace($OldHost, $appHost)); Write-Ok "README.md live link -> $appHost" }

Write-Host "`n==> Provisioned." -ForegroundColor Green
Write-Host "    App:   https://$appHost" -ForegroundColor White
Write-Host "    Vault: $vaultUri" -ForegroundColor White
Write-Host "    Next:  commit the appsettings + README change and push to master — deploy.yml ships the code." -ForegroundColor White
Write-Host "    The Azure CLI is left on $TargetAccount, so local runs read the new vault." -ForegroundColor White
