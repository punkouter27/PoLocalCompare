<#
.SYNOPSIS
    Fails when the repository has drifted from the conventions the 2026-09-10 clean-up pass set.

.DESCRIPTION
    Four checks, each of which exists because the corresponding defect had already happened and
    nothing warned about it:

      1. Test budget. AGENTS.md §8 fixes 100 / 50 / 25 / 25 cases per tier. The contract used to be
         prose that people remembered; three of the four tiers were over budget when it was first
         measured. Counts mirror how xUnit counts: [Fact] is one case, a [Theory] is one per
         [InlineData], and each E2EUI method carries [MemberData(nameof(Viewports))] and so runs
         once per viewport.

      2. Dead CSS classes. A class applied in .razor markup that no stylesheet defines is
         invisible: nothing warns, the element simply renders unstyled. The nav bar carried
         Bootstrap leftovers (`nav-item`, `btn-sm`) long after Bootstrap was removed, and five
         other classes styled nothing. Classes built by interpolation are skipped — they read as
         dead to any text scan but are real.

      3. Unused CSS rules. The reverse of 2: a class a stylesheet defines that no markup, C# or JS
         applies. They pile up silently when a feature or component goes and its rules stay.

      4. Cache-buster parity. Every stylesheet and script the browser loads must carry a `?v=`.
         Without one the browser serves the previous build from its HTTP cache and a change
         appears to do nothing — which cost real time when the box-sizing fix was briefly
         "verified" against a stale sheet.

.PARAMETER SkipTests
    Skip check 1 (it does not build or run tests, only counts attributes, so this is rarely useful).

.EXAMPLE
    pwsh SCRIPTS/check-clean.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()

function Write-Header([string]$text) {
    Write-Host ''
    Write-Host "── $text " -NoNewline
    Write-Host ('─' * [Math]::Max(0, 68 - $text.Length)) -ForegroundColor DarkGray
}

function Get-SourceFiles([string]$relativeDir, [string[]]$include) {
    $root = Join-Path $repoRoot $relativeDir
    if (-not (Test-Path $root)) { return @() }
    # Match either separator. CI runs this under pwsh on ubuntu-latest, where FullName uses '/',
    # so a Windows-only pattern would silently stop excluding build output and this script would
    # start counting generated files. The script is run on both platforms by design.
    Get-ChildItem $root -Recurse -File -Include $include |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
}

# ── 1. Test budget ────────────────────────────────────────────────────────────

if (-not $SkipTests) {
    Write-Header 'Test budget (AGENTS.md §8: 100 / 50 / 25 / 25)'

    $budgets = [ordered]@{ Unit = 100; Integration = 50; E2EAPI = 25; E2EUI = 25 }

    foreach ($tier in $budgets.Keys) {
        $files = Get-SourceFiles "tests/PoLocalCompare.$tier" @('*.cs')
        if ($files.Count -eq 0) {
            $failures.Add("Test tier 'PoLocalCompare.$tier' has no source files.")
            continue
        }

        $facts = 0; $inline = 0; $viewportData = 0
        foreach ($file in $files) {
            $text = Get-Content $file.FullName -Raw
            $facts += ([regex]::Matches($text, '\[Fact\]')).Count
            $inline += ([regex]::Matches($text, '\[InlineData')).Count
            $viewportData += ([regex]::Matches($text, '\[MemberData\(nameof\(Viewports\)\)\]')).Count
        }

        # A [Fact] is 1 case; a [Theory] contributes one per [InlineData]; an E2EUI
        # [MemberData(nameof(Viewports))] theory runs once per viewport (two).
        $cases = $facts + $inline + ($viewportData * 2)
        $budget = $budgets[$tier]

        if ($cases -gt $budget) {
            Write-Host ("  {0,-12} {1,4} / {2,-4} OVER by {3}" -f $tier, $cases, $budget, ($cases - $budget)) -ForegroundColor Red
            $failures.Add("$tier has $cases cases, over the $budget budget by $($cases - $budget).")
        } else {
            Write-Host ("  {0,-12} {1,4} / {2,-4} ok (headroom {3})" -f $tier, $cases, $budget, ($budget - $cases)) -ForegroundColor Green
        }
    }
}

# ── 2. Dead CSS classes ───────────────────────────────────────────────────────

Write-Header 'Dead CSS classes (declared in markup, defined nowhere)'

$cssFiles = Get-SourceFiles 'src' @('*.css')
$allCss = ($cssFiles | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"

$razorFiles = Get-SourceFiles 'src' @('*.razor')
$dead = [System.Collections.Specialized.OrderedDictionary]::new()

foreach ($file in $razorFiles) {
    $text = Get-Content $file.FullName -Raw
    foreach ($match in [regex]::Matches($text, 'class="([^"]+)"')) {
        $value = $match.Groups[1].Value

        # Only a wholly literal class list can be checked. A value containing Razor syntax or a
        # ternary (`class="category @(x is not null ? "profile" : "")"`) is not a list of class
        # names at all — scanning it yields tokens like `is`, `not` and `null`, which is how the
        # first version of this script reported twelve phantom dead classes.
        if ($value -notmatch '^[\s\w-]+$') { continue }

        foreach ($token in ($value -split '\s+')) {
            # Interpolated (`tourney__status--@_tournament.Status`) names read as dead to a text
            # scan but are real; Radzen owns its own `rz-` classes.
            if ([string]::IsNullOrWhiteSpace($token)) { continue }
            if ($token -match '@' -or $token -match '^rz-') { continue }
            if ($token -notmatch '^[a-zA-Z][\w-]*$') { continue }

            # No lookbehind: `.rz-datatable.archive__grid` is a legitimate compound selector, so
            # requiring a non-word character before the dot misses real definitions. The
            # lookahead is the one that matters — it stops `.grid` matching `.grid-row`.
            if ($allCss -match "\.$([regex]::Escape($token))(?![\w-])") { continue }

            if (-not $dead.Contains($token)) { $dead[$token] = $file.Name }
        }
    }
}

if ($dead.Count -eq 0) {
    Write-Host '  none' -ForegroundColor Green
} else {
    foreach ($entry in $dead.GetEnumerator()) {
        Write-Host ("  {0}  (first seen in {1})" -f $entry.Key, $entry.Value) -ForegroundColor Yellow
    }
    $failures.Add("$($dead.Count) class(es) in .razor markup have no rule in any .css.")
}

# ── 3. Unused CSS rules ───────────────────────────────────────────────────────
# The reverse of check 2: a class a stylesheet defines that nothing applies. Eight of these had
# piled up by 2026-09-26 (.po-chip, .po-bg--ok, .arena--thermal-alert…) — dead weight nobody
# could delete with confidence, because nothing said they were dead.

Write-Header 'Unused CSS rules (defined in a stylesheet, applied nowhere)'

$usageFiles = Get-SourceFiles 'src' @('*.razor', '*.cs', '*.js', '*.html', '*.cshtml') |
    Where-Object { $_.Name -ne 'web-llm.js' }
$usage = ($usageFiles | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"

# Classes the framework applies, not our markup: NavLink's active state and Blazor's form states.
$frameworkClasses = @('active', 'valid', 'invalid', 'modified', 'validation-message')

$unused = [System.Collections.Specialized.OrderedDictionary]::new()
foreach ($file in $cssFiles) {
    # Comments mention file names (`app.css`, `wow.js`) that would read as class selectors.
    $css = [regex]::Replace((Get-Content $file.FullName -Raw), '(?s)/\*.*?\*/', '')
    foreach ($match in [regex]::Matches($css, '\.(?<name>[a-zA-Z_][\w-]*)')) {
        $name = $match.Groups['name'].Value
        if ($name -match '^rz-' -or $frameworkClasses -contains $name -or $unused.Contains($name)) { continue }
        if ($usage -match "(?<![\w-])$([regex]::Escape($name))(?![\w-])") { continue }

        # Built by interpolation (`tourney__status--@_tournament.Status`,
        # `arena__viewports--focus-@_mobileFocus`): some prefix ending in `-` appears in markup
        # followed by a Razor expression.
        $interpolated = $false
        for ($i = $name.IndexOf('-'); $i -gt 0; $i = $name.IndexOf('-', $i + 1)) {
            if ($usage.Contains($name.Substring(0, $i + 1) + '@')) { $interpolated = $true; break }
        }
        if ($interpolated) { continue }

        $unused[$name] = $file.Name
    }
}

if ($unused.Count -eq 0) {
    Write-Host '  none' -ForegroundColor Green
} else {
    foreach ($entry in $unused.GetEnumerator()) {
        Write-Host ("  .{0}  (in {1})" -f $entry.Key, $entry.Value) -ForegroundColor Yellow
    }
    $failures.Add("$($unused.Count) CSS class(es) are defined but applied nowhere.")
}

# ── 3b. Unreferenced types ────────────────────────────────────────────────────
# A type whose name appears nowhere but its own declaration is dead (ModelDiagState sat unused
# for a month). Text-only, so it errs toward silence: any other mention — a test, a comment,
# a use in the same file — counts. Static classes are skipped: they hold extension methods and
# [LoggerMessage] partials, which are called by member name, never by type name.

Write-Header 'Unreferenced types (named only in their own declaration)'

$csFiles = @(Get-SourceFiles 'src' @('*.cs', '*.razor', '*.cshtml')) + @(Get-SourceFiles 'tests' @('*.cs'))
$corpus = ($csFiles | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"

$orphans = [System.Collections.Generic.List[string]]::new()
foreach ($file in ($csFiles | Where-Object { $_.Extension -eq '.cs' -and $_.FullName -match '[\\/]src[\\/]' })) {
    $text = Get-Content $file.FullName -Raw
    foreach ($match in [regex]::Matches($text, '(?m)^\s*(?:public|internal)\s+(?<mods>(?:sealed\s+|static\s+|abstract\s+|partial\s+|readonly\s+)*)(?:class|record|struct|enum|interface)\s+(?<name>\w+)')) {
        if ($match.Groups['mods'].Value -match 'static') { continue }
        $name = $match.Groups['name'].Value
        if ($name -eq 'Program') { continue }
        if ([regex]::Matches($corpus, "(?<!\w)$([regex]::Escape($name))(?!\w)").Count -le 1) {
            $orphans.Add("$name  (in $($file.Name))")
        }
    }
}

if ($orphans.Count -eq 0) {
    Write-Host '  none' -ForegroundColor Green
} else {
    foreach ($item in $orphans) { Write-Host "  $item" -ForegroundColor Yellow }
    $failures.Add("$($orphans.Count) type(s) are named nowhere but their own declaration.")
}

# ── 4. Cache-buster parity ────────────────────────────────────────────────────

Write-Header 'Cache-buster parity (?v= on every loaded asset)'

$indexPath = Join-Path $repoRoot 'src/PoLocalCompare.Client/wwwroot/index.html'
$missing = [System.Collections.Generic.List[string]]::new()

if (Test-Path $indexPath) {
    $index = Get-Content $indexPath -Raw
    foreach ($match in [regex]::Matches($index, '(?:src|href)="(?<url>[^"]+\.(?:js|css))(?<query>[^"]*)"')) {
        $url = $match.Groups['url'].Value
        if ($url -notmatch '^_' -and $url -notmatch '^(https?:)?//' -and $match.Groups['query'].Value -notmatch '\?v=') {
            $missing.Add("index.html: $url")
        }
    }
}

# import()/new Worker() cache-busters live in the C# interop services and in JS.
$interop = Get-SourceFiles 'src/PoLocalCompare.Client/Services' @('*.cs')
foreach ($file in $interop) {
    $text = Get-Content $file.FullName -Raw
    foreach ($match in [regex]::Matches($text, "'/js/(?<name>[\w.-]+\.js)(?<query>[^']*)'")) {
        if ($match.Groups['query'].Value -notmatch '\?v=') {
            $missing.Add("$($file.Name): /js/$($match.Groups['name'].Value)")
        }
    }
}

if ($missing.Count -eq 0) {
    Write-Host '  all busted' -ForegroundColor Green
} else {
    foreach ($item in $missing) { Write-Host "  $item" -ForegroundColor Yellow }
    $failures.Add("$($missing.Count) asset(s) are loaded without a ?v= cache-buster.")
}

# ── Verdict ───────────────────────────────────────────────────────────────────

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host 'check-clean: OK' -ForegroundColor Green
    exit 0
}

Write-Host 'check-clean: FAILED' -ForegroundColor Red
foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
exit 1
