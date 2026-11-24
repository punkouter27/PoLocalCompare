<#
.SYNOPSIS
    One command for the before/after page a UI change ships with (AGENTS.md section 12, rule 18).

.DESCRIPTION
    1. Builds each side LOCALLY like scripts/run-e2eui.ps1 does: `dotnet publish -c Release
       -p:WasmApplicationEnvironmentName=Development` (so the guest sign-in exists), started with
       ASPNETCORE_ENVIRONMENT=Development on https://localhost:<Port>. `-Before` is a git ref (default HEAD~1, checked out
       in a temporary detached worktree); `-After` is a ref too, or the working tree when empty. `-BeforeDir` skips the
       before build and reads PNGs named `*_<w>_<screen>.png` (the v4 audit's naming) or `<screen>_<w>.png`.
       `-AfterDir` does the same for the after side (e.g. the shots-* folders a `-KeepWork` run leaves, to recompose
       without rebuilding). Never points at the live site.
    2. Shoots every screen at 1366x768 and 390x844 with the E2EUI project's Playwright, reduced motion on. Signed-in
       screens go through the dev guest door (/auth/login/fake), like the E2EUI tier.
    3. Compares before/after pixel by pixel in Chromium and boxes the changed areas in red on the after shot.
    4. Writes SCREENSHOTS/<yyMMdd>-<Feature>.html (self-contained; unchanged screens shown once, small) and the single
       PNG beside it, SCREENSHOTS/<yyMMdd>-<Feature>.png (the 4 most-changed screens, before | after, with the notes).

    Needs Docker for Azurite (started and removed here unless something already answers on 10002), Playwright
    Chromium, and `az login` for Key Vault (as run-e2eui.ps1).

.PARAMETER Screens
    name=in:/route (signed in as a guest) or name=out:/route (signed out).

.EXAMPLE
    pwsh -NoProfile -File ./scripts/screenshots.ps1 -Feature models-pill -Notes 'The models-ready pill reads 4.5:1.'
    pwsh -NoProfile -File ./scripts/screenshots.ps1 -Feature v4-audit -BeforeDir C:\shots\before -Before 8f10014
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Feature,
    [string]$Before = 'HEAD~1',
    [string]$BeforeDir = '',
    [string]$After = '',
    [string]$AfterDir = '',
    [string[]]$Screens = @('login=out:/login', 'diag=out:/diag', 'home=in:/', 'tournament=in:/tournament',
        'leaderboard=in:/leaderboard', 'archive=in:/archive', 'catalog=in:/catalog'),
    [string[]]$Notes = @(),
    [string]$Date = (Get-Date).ToUniversalTime().ToString('yyMMdd'),
    [int]$Port = 5011,
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot 'SCREENSHOTS'
$work = Join-Path ([IO.Path]::GetTempPath()) "polocalcompare-shots-$PID"
$baseUrl = "https://localhost:$Port"
$widths = @(@{ W = 1366; H = 768 }, @{ W = 390; H = 844 })
$screenList = foreach ($s in $Screens) {
    if ($s -notmatch '^([a-z0-9-]+)=(in|out):(/\S*)$') { throw "Screen '$s' is not name=in:/route or name=out:/route" }
    [pscustomobject]@{ Name = $Matches[1]; SignedIn = $Matches[2] -eq 'in'; Route = $Matches[3] }
}
function Test-Port([int]$p) {
    $c = [Net.Sockets.TcpClient]::new()
    try { $c.ConnectAsync('127.0.0.1', $p).Wait(1000) -and $c.Connected } catch { $false } finally { $c.Dispose() }
}
if (Test-Port $Port) { throw "Something already listens on $Port; pass -Port." }

$pwDll = Join-Path $repoRoot 'tests/PoLocalCompare.E2EUI/bin/Release/net10.0/Microsoft.Playwright.dll'
if (-not (Test-Path $pwDll)) {
    dotnet build (Join-Path $repoRoot 'tests/PoLocalCompare.E2EUI') -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'building tests/PoLocalCompare.E2EUI failed' }
}
Add-Type -Path $pwDll
function Wait-Task($task) { $task.GetAwaiter().GetResult() }

New-Item -ItemType Directory -Force $work | Out-Null
$startedAzurite = $false
$worktrees = @()
$pw = $null; $browser = $null
try {
    if (-not (Test-Port 10002)) {
        docker compose -f (Join-Path $repoRoot 'docker-compose.yml') up -d azurite | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Azurite did not start. Is Docker running?' }
        $startedAzurite = $true
    }
    $pw = Wait-Task ([Microsoft.Playwright.Playwright]::CreateAsync())
    $browser = Wait-Task ($pw.Chromium.LaunchAsync([Microsoft.Playwright.BrowserTypeLaunchOptions]@{ Headless = $true }))

    function Get-Source([string]$ref, [string]$side) {
        if (-not $ref) { return $repoRoot }
        $dir = Join-Path $work "src-$side"
        git -C $repoRoot worktree add --detach $dir $ref 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "git worktree add $ref failed" }
        $script:worktrees += $dir
        return $dir
    }

    function Invoke-Capture([string]$src, [string]$side) {
        $pub = Join-Path $work "pub-$side"
        Write-Host "[$side] publishing $src" -ForegroundColor Cyan
        dotnet publish (Join-Path $src 'src/PoLocalCompare.Api') -c Release -o $pub -p:WasmApplicationEnvironmentName=Development --nologo -v q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "[$side] publish failed" }
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $env:ASPNETCORE_URLS = $baseUrl
        $app = Start-Process dotnet -ArgumentList 'PoLocalCompare.Api.dll' -WorkingDirectory $pub -PassThru -NoNewWindow `
            -RedirectStandardOutput (Join-Path $work "$side.out.log") -RedirectStandardError (Join-Path $work "$side.err.log")
        $shots = Join-Path $work "shots-$side"
        New-Item -ItemType Directory -Force $shots | Out-Null
        try {
            $handler = [Net.Http.HttpClientHandler]::new()
            $handler.ServerCertificateCustomValidationCallback = [Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
            $http = [Net.Http.HttpClient]::new($handler)
            $up = $false
            foreach ($i in 1..90) {
                if ($app.HasExited) { throw "[$side] the app exited during startup; see $work\$side.out.log" }
                try { if ($http.GetAsync("$baseUrl/health/live").Result.IsSuccessStatusCode) { $up = $true; break } } catch { }
                Start-Sleep -Seconds 2
            }
            if (-not $up) { throw "[$side] the app did not answer $baseUrl/health/live within 180 s" }
            foreach ($s in $screenList) {
                foreach ($v in $widths) {
                    $ctx = Wait-Task ($browser.NewContextAsync([Microsoft.Playwright.BrowserNewContextOptions]@{
                        ViewportSize = [Microsoft.Playwright.ViewportSize]@{ Width = $v.W; Height = $v.H }
                        IgnoreHTTPSErrors = $true; DeviceScaleFactor = 1; ReducedMotion = [Microsoft.Playwright.ReducedMotion]::Reduce
                    }))
                    try {
                        $page = Wait-Task ($ctx.NewPageAsync())
                        $url = if ($s.SignedIn) { "$baseUrl/auth/login/fake?user=$([uri]::EscapeDataString('e2e.judge@example.com'))&returnUrl=$([uri]::EscapeDataString($s.Route))" } else { "$baseUrl$($s.Route)" }
                        Wait-Task ($page.GotoAsync($url, [Microsoft.Playwright.PageGotoOptions]@{ WaitUntil = [Microsoft.Playwright.WaitUntilState]::NetworkIdle; Timeout = 90000 })) | Out-Null
                        Wait-Task ($page.WaitForSelectorAsync('.po-header', [Microsoft.Playwright.PageWaitForSelectorOptions]@{ Timeout = 60000 })) | Out-Null
                        if ($s.Route -eq '/diag') {
                            try { Wait-Task ($page.WaitForFunctionAsync("() => !document.querySelector('[data-testid=diag-banner]')?.textContent.includes('Checking')", $null, [Microsoft.Playwright.PageWaitForFunctionOptions]@{ Timeout = 60000 })) | Out-Null } catch { }
                        }
                        Start-Sleep -Milliseconds 2500
                        Wait-Task ($page.ScreenshotAsync([Microsoft.Playwright.PageScreenshotOptions]@{ Path = (Join-Path $shots "$($s.Name)_$($v.W).png") })) | Out-Null
                        Write-Host "[$side] $($s.Name) $($v.W)"
                    }
                    finally { Wait-Task ($ctx.CloseAsync()) | Out-Null }
                }
            }
        }
        finally { if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force } }
        return $shots
    }

    $beforeShots = if ($BeforeDir) { $BeforeDir } else { Invoke-Capture (Get-Source $Before 'before') 'before' }
    $afterShots = if ($AfterDir) { $AfterDir } else { Invoke-Capture (Get-Source $After 'after') 'after' }

    # ── Compare, annotate, compose ───────────────────────────────────────────
    $tool = Wait-Task ($browser.NewPageAsync())
    $diffJs = @'
async ([a, b]) => {
  const load = src => new Promise((ok, no) => { const i = new Image(); i.onload = () => ok(i); i.onerror = no; i.src = src; });
  const [ia, ib] = await Promise.all([load(a), load(b)]);
  const w = Math.max(ia.width, ib.width), h = Math.max(ia.height, ib.height);
  const pix = img => { const c = new OffscreenCanvas(w, h); const x = c.getContext('2d'); x.drawImage(img, 0, 0); return x.getImageData(0, 0, w, h).data; };
  const pa = pix(ia), pb = pix(ib);
  const CELL = 16, gw = Math.ceil(w / CELL), gh = Math.ceil(h / CELL), grid = new Uint8Array(gw * gh);
  let changed = 0;
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const i = (y * w + x) * 4;
    if (Math.abs(pa[i] - pb[i]) + Math.abs(pa[i + 1] - pb[i + 1]) + Math.abs(pa[i + 2] - pb[i + 2]) > 48) { changed++; grid[((y / CELL) | 0) * gw + ((x / CELL) | 0)] = 1; }
  }
  const seen = new Uint8Array(gw * gh), boxes = [];
  for (let s = 0; s < grid.length; s++) {
    if (!grid[s] || seen[s]) continue;
    let x0 = 1e9, y0 = 1e9, x1 = -1, y1 = -1; const st = [s]; seen[s] = 1;
    while (st.length) {
      const c = st.pop(), cx = c % gw, cy = (c / gw) | 0;
      x0 = Math.min(x0, cx); y0 = Math.min(y0, cy); x1 = Math.max(x1, cx); y1 = Math.max(y1, cy);
      for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) {
        const nx = cx + dx, ny = cy + dy, n = ny * gw + nx;
        if (nx >= 0 && ny >= 0 && nx < gw && ny < gh && grid[n] && !seen[n]) { seen[n] = 1; st.push(n); }
      }
    }
    boxes.push([x0 * CELL, y0 * CELL, (x1 + 1) * CELL, (y1 + 1) * CELL]);
  }
  const c = new OffscreenCanvas(ib.width, ib.height), x = c.getContext('2d');
  x.drawImage(ib, 0, 0); x.strokeStyle = '#e00'; x.lineWidth = 3;
  for (const [l, t, r, btm] of boxes) x.strokeRect(l - 3, t - 3, r - l + 6, btm - t + 6);
  const blob = await c.convertToBlob({ type: 'image/png' });
  const marked = await new Promise(ok => { const f = new FileReader(); f.onload = () => ok(f.result); f.readAsDataURL(blob); });
  return JSON.stringify({ pct: Math.round(changed * 10000 / (w * h)) / 100, boxes: boxes.length, marked });
}
'@
    $enc = [Net.WebUtility]
    $rows = foreach ($s in $screenList) {
        foreach ($v in $widths) {
            $a = Join-Path $afterShots "$($s.Name)_$($v.W).png"
            if (-not (Test-Path $a)) { Write-Warning "no after shot $a"; continue }
            $b = Get-ChildItem $beforeShots -Filter "*.png" | Where-Object { $_.Name -eq "$($s.Name)_$($v.W).png" -or $_.Name -like "*_$($v.W)_$($s.Name -replace '-', '').png" } | Select-Object -First 1
            $bUri = if ($b) { 'data:image/png;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($b.FullName)) } else { $null }
            $aUri = 'data:image/png;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($a))
            $d = if ($bUri) { Wait-Task ($tool.EvaluateAsync[string]($diffJs, [string[]]@($bUri, $aUri))) | ConvertFrom-Json } else { $null }
            [pscustomobject]@{ Screen = $s; W = $v.W; H = $v.H; Before = $bUri; After = $aUri; Diff = $d; Changed = (-not $d) -or $d.pct -gt 0 }
        }
    }

    $title = "$Date $Feature"
    $beforeLabel = if ($BeforeDir) { "PNGs from $(Split-Path $BeforeDir -Leaf) (git $Before)" } else { "git $Before ($(git -C $repoRoot rev-parse --short $Before))" }
    $afterLabel = if ($AfterDir) { "PNGs from $(Split-Path $AfterDir -Leaf)$(if ($After) { " (git $After)" })" } elseif ($After) { "git $After ($(git -C $repoRoot rev-parse --short $After))" } else { "working tree on $(git -C $repoRoot rev-parse --short HEAD)" }
    $css = 'body{font:14px system-ui,sans-serif;margin:16px;background:#fff;color:#111}h1{font-size:20px}h2{font-size:16px;margin:20px 0 6px}' +
        '.pair{display:flex;gap:12px;align-items:flex-start;flex-wrap:wrap}figure{margin:0}figcaption{font-weight:600;margin-bottom:4px}' +
        'img{border:1px solid #bbb;display:block}.same{color:#555}.chg{color:#b00;font-weight:600}'
    function Format-Page([object[]]$items, [double]$deskScale, [double]$phoneScale, [bool]$all) {
        $sb = [Text.StringBuilder]::new()
        [void]$sb.Append("<!doctype html><html lang=`"en`"><head><meta charset=`"utf-8`"><title>$($enc::HtmlEncode($title))</title><style>$css</style></head><body>")
        [void]$sb.Append("<h1>$($enc::HtmlEncode($title)): before / after</h1><p>Before: $($enc::HtmlEncode($beforeLabel)). After: $($enc::HtmlEncode($afterLabel)). Local Release publish, 1366x768 and 390x844, signed-in screens as the dev guest; red boxes mark changed pixels.</p>")
        if ($Notes) { [void]$sb.Append('<ul>' + (($Notes | ForEach-Object { "<li>$($enc::HtmlEncode($_))</li>" }) -join '') + '</ul>') }
        foreach ($r in $items) {
            $wpx = [int]($r.W * $(if ($r.W -gt 768) { $deskScale } else { $phoneScale }))
            $what = if (-not $r.Diff) { '<span class="chg">no before shot</span>' }
                elseif ($r.Changed) { "<span class=`"chg`">changed: $($r.Diff.pct)% of pixels, $($r.Diff.boxes) area(s)</span>" }
                else { '<span class="same">no visible change</span>' }
            [void]$sb.Append("<h2>$($r.Screen.Name) ($($enc::HtmlEncode($r.Screen.Route)), signed $(if ($r.Screen.SignedIn) { 'in' } else { 'out' })) at $($r.W)x$($r.H): $what</h2><div class=`"pair`">")
            if ($r.Changed -or $all) {
                if ($r.Before) { [void]$sb.Append("<figure><figcaption>Before</figcaption><img width=`"$wpx`" alt=`"before`" src=`"$($r.Before)`"></figure>") }
                $shown = if ($r.Diff) { $r.Diff.marked } else { $r.After }
                [void]$sb.Append("<figure><figcaption>After</figcaption><img width=`"$wpx`" alt=`"after`" src=`"$shown`"></figure>")
            }
            else { [void]$sb.Append("<figure><figcaption>Both</figcaption><img width=`"$([int]($wpx / 2))`" alt=`"unchanged`" src=`"$($r.After)`"></figure>") }
            [void]$sb.Append('</div>')
        }
        [void]$sb.Append('</body></html>')
        $sb.ToString()
    }

    New-Item -ItemType Directory -Force $outDir | Out-Null
    $html = Join-Path $outDir "$Date-$Feature.html"
    $png = Join-Path $outDir "$Date-$Feature.png"
    [IO.File]::WriteAllText($html, (Format-Page $rows 0.5 0.6 $false), [Text.UTF8Encoding]::new($false))

    # The single PNG stays readable and small: the 4 most-changed screens (the HTML has all of them).
    $pngRows = @($rows | Where-Object Changed | Sort-Object { if ($_.Diff) { $_.Diff.pct } else { 100 } } -Descending | Select-Object -First 4)
    if (-not $pngRows) { $pngRows = @($rows | Select-Object -First 2) }
    $more = @($rows | Where-Object Changed).Count - $pngRows.Count
    if ($more -gt 0) { $Notes += "PNG: the $($pngRows.Count) most-changed screens; $more more changed screen(s) are in the HTML page." }
    foreach ($scale in 0.45, 0.35, 0.28, 0.22) {
        $shot = Wait-Task ($browser.NewPageAsync([Microsoft.Playwright.BrowserNewPageOptions]@{ ViewportSize = [Microsoft.Playwright.ViewportSize]@{ Width = 1300; Height = 800 } }))
        Wait-Task ($shot.SetContentAsync((Format-Page $pngRows $scale ($scale * 1.4) $true))) | Out-Null
        Wait-Task ($shot.ScreenshotAsync([Microsoft.Playwright.PageScreenshotOptions]@{ Path = $png; FullPage = $true })) | Out-Null
        Wait-Task ($shot.CloseAsync()) | Out-Null
        if ((Get-Item $png).Length -le 300KB) { break }
    }
    Write-Host ''
    Write-Host "Page: $html ($([int]((Get-Item $html).Length / 1KB)) KB)" -ForegroundColor Green
    Write-Host "PNG:  $png ($([int]((Get-Item $png).Length / 1KB)) KB)" -ForegroundColor Green
    $rows | ForEach-Object { "{0,-12} {1,4} {2}" -f $_.Screen.Name, $_.W, $(if (-not $_.Diff) { 'no before' } elseif ($_.Changed) { "changed $($_.Diff.pct)%" } else { 'same' }) }
}
finally {
    if ($browser) { Wait-Task ($browser.CloseAsync()) | Out-Null }
    if ($pw) { $pw.Dispose() }
    foreach ($w in $worktrees) { git -C $repoRoot worktree remove --force $w 2>&1 | Out-Null }
    if ($startedAzurite) { docker compose -f (Join-Path $repoRoot 'docker-compose.yml') rm --stop --force azurite 2>&1 | Out-Null }
    if (-not $KeepWork) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}
