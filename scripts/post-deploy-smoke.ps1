<#
.SYNOPSIS
    Checks the live site after the shared deploy.yml health gate has passed.

.DESCRIPTION
    1. SPA mount: index.html carries the Blazor bootstrap and _framework/blazor.webassembly.js is
       served (catches trimmed/broken WASM publishes that /health cannot see).
    2. Freshness: the css/app.css?v= cache-buster the live page serves equals the one in this
       build's index.html. In the deploy job that is the downloaded package (publish/wwwroot);
       elsewhere it falls back to the source file.
    3. Auth gate: anonymous calls to the API groups answer 401. Authorization is deny-by-default
       (FallbackPolicy), so a 200 means a group lost its RequireAuthorization().

.EXAMPLE
    pwsh ./scripts/post-deploy-smoke.ps1 -BaseUrl https://<app>.azurewebsites.net
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BaseUrl)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$BaseUrl = $BaseUrl.TrimEnd('/')

function Invoke-Get([string]$Path) {
    try { return Invoke-WebRequest -Uri "$BaseUrl$Path" -TimeoutSec 30 -SkipHttpErrorCheck -MaximumRedirection 0 -ErrorAction Stop }
    catch { return $null }
}

function Fail([string]$Message) { Write-Host "::error::$Message"; exit 1 }

# 1. SPA mount
$root = Invoke-Get '/'
$rootHtml = if ($root) { [string]$root.Content } else { '' }
if ($rootHtml -notmatch 'blazor\.webassembly\.js') { Fail 'index.html is missing the Blazor bootstrap' }
$boot = Invoke-Get '/_framework/blazor.webassembly.js'
$bootCode = if ($boot) { [int]$boot.StatusCode } else { 0 }
Write-Host "Blazor bootstrap asset: HTTP $bootCode"
if ($bootCode -ne 200) { Fail 'Blazor framework assets are not being served' }

# 2. Freshness
$indexPath = @('publish/wwwroot/index.html', 'src/PoLocalCompare.Client/wwwroot/index.html') |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $indexPath) { Fail 'No index.html to read the expected cache-buster from' }
$pattern = 'css/app\.css\?v=[0-9]*'
$expected = ([regex]::Match((Get-Content $indexPath -Raw), $pattern)).Value
$served = ([regex]::Match($rootHtml, $pattern)).Value
Write-Host "cache-buster in build ($indexPath): '$expected' / served: '$served'"
if (-not $expected -or $expected -ne $served) { Fail "live index.html is stale (served '$served', expected '$expected')" }

# 3. Auth gate
foreach ($path in '/api/models', '/api/leaderboard', '/api/duels') {
    $r = Invoke-Get $path
    $code = if ($r) { [int]$r.StatusCode } else { 0 }
    Write-Host "anonymous ${path}: HTTP $code"
    if ($code -ne 401) { Fail "anonymous $path returned $code, expected 401" }
}

Write-Host 'Deployment verified: freshness, auth gate and SPA assets OK.'
