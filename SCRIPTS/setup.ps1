#Requires -Version 7
<#
.SYNOPSIS
    One-command setup for PoLocalCompare on a new Windows machine.

.DESCRIPTION
    Installs prerequisites via Winget, starts Docker/Azurite, and configures
    local mock keys so the app runs first-time without manual steps.

    Run from the repo root:
        pwsh SCRIPTS/setup.ps1

.NOTES
    Standards §9 & §10 — One-Command Setup & Portability.
#>

[CmdletBinding()]
param (
    [switch]$SkipWinget,
    [switch]$SkipDocker,
    [switch]$SkipModels
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step([string]$Msg) { Write-Host "`n==> $Msg" -ForegroundColor Cyan }
function Write-Ok([string]$Msg)   { Write-Host "    [OK] $Msg" -ForegroundColor Green }
function Write-Warn([string]$Msg) { Write-Host "    [WARN] $Msg" -ForegroundColor Yellow }

function Clear-DotnetPorts {
    param(
        [int[]]$Ports = @(5000, 5001)
    )

    foreach ($port in $Ports) {
        try {
            $connections = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
            if (-not $connections) {
                continue
            }

            $pids = $connections | Select-Object -ExpandProperty OwningProcess -Unique
            foreach ($pid in $pids) {
                try {
                    $proc = Get-Process -Id $pid -ErrorAction Stop
                    if ($proc.ProcessName -eq 'dotnet') {
                        Stop-Process -Id $pid -Force -ErrorAction Stop
                        Write-Ok "Stopped orphaned dotnet process on port $port (PID: $pid)"
                    }
                }
                catch {
                    Write-Warn "Could not inspect or stop process PID $pid for port $port"
                }
            }
        }
        catch {
            Write-Warn "Could not evaluate listeners on port $port"
        }
    }
}

# ─── 1. Prerequisites via Winget ─────────────────────────────────────────────
if (-not $SkipWinget) {
    Write-Step "Installing prerequisites via Winget"

    $packages = @(
        @{ Id = 'Microsoft.DotNet.SDK.10';         Name = '.NET 10 SDK' },
        @{ Id = 'Docker.DockerDesktop';             Name = 'Docker Desktop' },
        @{ Id = 'Python.Python.3.12';               Name = 'Python 3.12' },
        @{ Id = 'Microsoft.AzureCLI';               Name = 'Azure CLI' }
    )

    foreach ($pkg in $packages) {
        $installed = winget list --id $pkg.Id --exact --accept-source-agreements 2>$null |
                     Select-String $pkg.Id
        if ($installed) {
            Write-Ok "$($pkg.Name) already installed"
        } else {
            Write-Host "    Installing $($pkg.Name)..." -ForegroundColor DarkCyan
            winget install --id $pkg.Id --exact --silent --accept-package-agreements --accept-source-agreements
            Write-Ok "$($pkg.Name) installed"
        }
    }
}

# ─── 1.5 Azure CLI login check (Key Vault access) ───────────────────────────
Write-Step "Checking Azure CLI login"
try {
    $accountJson = az account show --output json 2>$null
    if (-not $accountJson) {
        Write-Warn "Not logged in to Azure CLI. Running 'az login' for Key Vault access..."
        az login | Out-Null
    }
    else {
        Write-Ok "Azure CLI is already authenticated"
    }
}
catch {
    Write-Warn "Azure CLI login check failed. Run 'az login' manually if Key Vault access fails."
}

# ─── 1.6 Free required local ports (5000/5001) ───────────────────────────────
Write-Step "Ensuring ports 5000/5001 are free"
Clear-DotnetPorts -Ports @(5000, 5001)

# ─── 2. Docker + Azurite ──────────────────────────────────────────────────────
if (-not $SkipDocker) {
    Write-Step "Starting Azurite (Azure Storage emulator) via Docker"

    $dockerRunning = docker info 2>$null
    if (-not $dockerRunning) {
        Write-Warn "Docker Desktop is not running. Please start it and re-run this script."
        exit 1
    }

    $azuriteRunning = docker ps --filter "name=azurite" --format "{{.Names}}" 2>$null
    if ($azuriteRunning -match 'azurite') {
        Write-Ok "Azurite container already running"
    } else {
        $azuriteExists = docker ps -a --filter "name=azurite" --format "{{.Names}}" 2>$null
        if ($azuriteExists -match 'azurite') {
            docker start azurite | Out-Null
            Write-Ok "Azurite container started"
        } else {
            docker run -d --name azurite `
                -p 10000:10000 -p 10002:10002 `
                mcr.microsoft.com/azure-storage/azurite | Out-Null
            Write-Ok "Azurite container created and started"
        }
    }
}

# ─── 3. Local mock / dev keys ─────────────────────────────────────────────────
Write-Step "Configuring local dev settings"

$devSettings = Join-Path $RepoRoot 'src\PoLocalCompare.Api\appsettings.Development.json'
if (-not (Test-Path $devSettings)) {
    $template = @{
        Logging = @{
            LogLevel = @{
                Default             = 'Debug'
                'Microsoft.AspNetCore' = 'Information'
            }
        }
        KeyVault = @{
            Uri = 'https://kv-poshared2.vault.azure.net/'
        }
        ApplicationInsights = @{
            ConnectionString = ''
        }
        ConnectionStrings = @{
            AzureTableStorage = 'UseDevelopmentStorage=true'
            AzureBlobStorage  = 'UseDevelopmentStorage=true'
        }
        Features = @{
            UseRealAi = $false
        }
    } | ConvertTo-Json -Depth 5
    $template | Set-Content $devSettings -Encoding UTF8
    Write-Ok "Created appsettings.Development.json with Azurite defaults"
} else {
    Write-Ok "appsettings.Development.json already exists"
}

# ─── 4. Restore .NET dependencies ────────────────────────────────────────────
Write-Step "Restoring .NET packages"
Push-Location $RepoRoot
try {
    dotnet restore PoLocalCompare.slnx --verbosity quiet
    Write-Ok "Packages restored"
} finally {
    Pop-Location
}

# ─── 4.5 Ecosystem repos + absolute tool (standards §3.4) ───────────────────
Write-Step "Ensuring ecosystem repositories and tools"

$ecosystemRoot = Split-Path -Parent $RepoRoot
$ecosystemRepos = @(
    @{ Name = 'gstack';              Url = 'https://github.com/punkouter26/gstack.git' },
    @{ Name = 'Understand-Anything'; Url = 'https://github.com/punkouter26/Understand-Anything.git' },
    @{ Name = 'graphify';            Url = 'https://github.com/punkouter26/graphify.git' }
)
foreach ($repo in $ecosystemRepos) {
    $target = Join-Path $ecosystemRoot $repo.Name
    if (Test-Path $target) {
        Write-Ok "$($repo.Name) already present at $target"
    } else {
        try {
            git clone --depth 1 $repo.Url $target 2>$null | Out-Null
            Write-Ok "$($repo.Name) cloned to $target"
        }
        catch {
            Write-Warn "Could not clone $($repo.Name) from $($repo.Url) — clone it manually if needed"
        }
    }
}

# absolute (https://github.com/maddhruv/absolute) — installed globally via npm.
if (Get-Command absolute -ErrorAction SilentlyContinue) {
    Write-Ok "absolute tool already installed"
} elseif (Get-Command npm -ErrorAction SilentlyContinue) {
    try {
        npm install --global github:maddhruv/absolute | Out-Null
        Write-Ok "absolute tool installed"
    }
    catch {
        Write-Warn "npm install of absolute failed — install manually: npm i -g github:maddhruv/absolute"
    }
} else {
    Write-Warn "npm not found — install Node.js, then: npm i -g github:maddhruv/absolute"
}

# ─── 5. Download local LLM models (optional) ─────────────────────────────────
if (-not $SkipModels) {
    Write-Step "Downloading WebLLM models (this may take a while; skip with -SkipModels)"
    $downloadScript = Join-Path $PSScriptRoot 'download-models.py'
    if (Test-Path $downloadScript) {
        python $downloadScript
        Write-Ok "Models downloaded"
    } else {
        Write-Warn "download-models.py not found — skipping model download"
    }
}

# ─── 6. Ollama context window ────────────────────────────────────────────────
# OllamaInferenceProxy asks for up to 4096 output tokens over the OpenAI-compatible /v1 route,
# which cannot set num_ctx per request. Ollama's default context is 4096 TOTAL — system prompt,
# user prompt, any thinking and the document — so long pages were cut off (finish_reason=length,
# DuelResult.WasTruncated). The server-wide setting is the only knob; Ollama must restart to read it.
Write-Step "Setting Ollama context window"
if ([Environment]::GetEnvironmentVariable('OLLAMA_CONTEXT_LENGTH', 'User') -ne '8192') {
    [Environment]::SetEnvironmentVariable('OLLAMA_CONTEXT_LENGTH', '8192', 'User')
    Write-Ok "OLLAMA_CONTEXT_LENGTH=8192 (restart Ollama to apply)"
} else {
    Write-Ok "OLLAMA_CONTEXT_LENGTH already 8192"
}

# ─── Done ─────────────────────────────────────────────────────────────────────
Write-Host "`n==> Setup complete!" -ForegroundColor Green
Write-Host "    Run the app:  dotnet watch run --project src/PoLocalCompare.Api/PoLocalCompare.Api.csproj --launch-profile https" -ForegroundColor White
Write-Host "    Open:         https://localhost:5001" -ForegroundColor White
