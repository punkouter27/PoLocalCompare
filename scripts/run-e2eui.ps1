<#
.SYNOPSIS
    Runs the E2E-UI tier against the Release publish output — the same build deploy.yml ships.

.DESCRIPTION
    1. dotnet publish src/PoLocalCompare.Api -c Release into a temp folder. .NET 10 bakes the Blazor
       WebAssembly environment into the client at publish time, so a plain Release publish boots the
       client as Production (no guest sign-in, no dev persona menu). The browser tier publishes with
       -p:WasmApplicationEnvironmentName=Development; everything else (Release, compression, the
       PublishTrimmed setting in the Client csproj) is exactly what deploys.
    2. Starts Azurite (docker compose up -d azurite) unless something already answers on 10002.
    3. Starts the published app (dotnet PoLocalCompare.Api.dll, ASPNETCORE_ENVIRONMENT=Development) at
       https://localhost:5001 and waits for /health/live.
    4. Runs tests/PoLocalCompare.E2EUI (Release, HEADLESS=1 unless -Headed).
    5. Stops the app, and Azurite too when this script started it. Exit code = the test run's.

.PARAMETER Headed
    Show the browser windows (default: headless).

.PARAMETER Filter
    Passed to dotnet test --filter.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/run-e2eui.ps1
#>
[CmdletBinding()]
param(
    [switch]$Headed,
    [string]$Filter
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path ([IO.Path]::GetTempPath()) 'polocalcompare-e2eui-publish'
$baseUrl = 'https://localhost:5001'

function Test-Port([int]$port) {
    $client = [Net.Sockets.TcpClient]::new()
    try { $client.ConnectAsync('127.0.0.1', $port).Wait(1000) -and $client.Connected } catch { $false } finally { $client.Dispose() }
}

if (Test-Port 5001) { throw "Something already listens on 5001. Stop it first: this script starts its own app there." }

Write-Host "Publishing Release to $publishDir"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $repoRoot 'src/PoLocalCompare.Api') -c Release -o $publishDir -p:WasmApplicationEnvironmentName=Development --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$startedAzurite = $false
if (-not (Test-Port 10002)) {
    Write-Host "Starting Azurite (docker compose up -d azurite)"
    docker compose -f (Join-Path $repoRoot 'docker-compose.yml') up -d azurite
    if ($LASTEXITCODE -ne 0) { throw "Azurite did not start. Is Docker running?" }
    $startedAzurite = $true
}

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = $baseUrl
$logFile = Join-Path $publishDir 'app.log'
$app = Start-Process dotnet -ArgumentList 'PoLocalCompare.Api.dll' -WorkingDirectory $publishDir -PassThru -NoNewWindow `
    -RedirectStandardOutput $logFile -RedirectStandardError (Join-Path $publishDir 'app.err.log')
$exit = 1
try {
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.ServerCertificateCustomValidationCallback = [Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
    $http = [Net.Http.HttpClient]::new($handler)
    $up = $false
    foreach ($i in 1..90) {
        if ($app.HasExited) { throw "The app exited during startup; see $logFile" }
        try { if ($http.GetAsync("$baseUrl/health/live").Result.IsSuccessStatusCode) { $up = $true; break } } catch { }
        Start-Sleep -Seconds 2
    }
    if (-not $up) { throw "The app did not answer $baseUrl/health/live within 180 s; see $logFile" }
    Write-Host "App (Release publish) is up at $baseUrl"

    $env:BASE_URL = $baseUrl
    $env:HEADLESS = if ($Headed) { '0' } else { '1' }
    $testArgs = @('test', (Join-Path $repoRoot 'tests/PoLocalCompare.E2EUI'), '-c', 'Release')
    if ($Filter) { $testArgs += @('--filter', $Filter) }
    dotnet @testArgs
    $exit = $LASTEXITCODE
}
finally {
    if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
    if ($startedAzurite) { docker compose -f (Join-Path $repoRoot 'docker-compose.yml') stop azurite | Out-Null }
}
exit $exit
