<#
.SYNOPSIS
    Bumps the plugin version in Directory.Build.props, build.yaml, manifest.json
    and scaffolds a new CHANGELOG.md section.

.DESCRIPTION
    Reads the current version from Directory.Build.props, validates the new
    version, cross-checks it against build.yaml / manifest.json / CHANGELOG.md,
    then stages updates in memory and writes all four files at the end so a
    mid-run error cannot leave the repo half-bumped.

    When invoked interactively, the script asks whether to reuse the current
    Jellyfin target ABI or pick a new one. Pass -TargetAbi or -NonInteractive
    to skip the prompt.

    Unless -NoFetch is passed, the script fetches live badge values (backend
    and frontend coverage via Shields.io, e2e count and minimum Jellyfin
    version from README badges) and seeds the changelog scaffolds with them.
    Content bullets stay as "TBD" so a grep catches anything you forgot to fill
    in before cutting a release.

.PARAMETER NewVersion
    New 4-part version, e.g. 3.1.0.1. Must match ^\d+\.\d+\.\d+\.\d+$ and be
    strictly greater than the current version.

.PARAMETER TargetAbi
    Jellyfin target ABI for the new manifest.json entry. When omitted, the
    script prompts interactively; with -NonInteractive it defaults to the
    targetAbi of the current topmost manifest entry.

.PARAMETER ReleaseDate
    ISO date (YYYY-MM-DD) for the CHANGELOG heading. Defaults to today (UTC).
    Validated with DateTime.ParseExact so '9999-99-99' is rejected.

.PARAMETER NonInteractive
    Never prompt. Use -TargetAbi explicitly or fall back to the current ABI.

.PARAMETER NoFetch
    Skip the live badge fetch. Scaffolds will contain 'TBD' for coverage and
    test counts.

.PARAMETER DryRun
    Print the plan and intended edits without writing to any file.

.EXAMPLE
    pwsh scripts/bump-version.ps1 -NewVersion 3.1.0.1

.EXAMPLE
    pwsh scripts/bump-version.ps1 -NewVersion 3.2.0.0 -TargetAbi 12.5.0.0 -DryRun

.EXAMPLE
    pwsh scripts/bump-version.ps1 -NewVersion 3.1.0.1 -NonInteractive -NoFetch
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $NewVersion,

    [string] $TargetAbi,

    [string] $ReleaseDate,

    [switch] $NonInteractive,

    [switch] $NoFetch,

    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# InvariantCulture pins format separators (`:` stays `:` on da-DK, not `.`) and
# the Gregorian calendar (so a Buddhist culture like th-TH does not emit a
# year 2569 for "yyyy"). Every DateTime.ToString / .Parse in this script must
# use it, otherwise the script would write locale-mangled timestamps into
# manifest.json or a wrong year into CHANGELOG.md.
$script:Invariant = [System.Globalization.CultureInfo]::InvariantCulture

# ========================================================================
# Helpers (defined first so StrictMode never sees a forward reference)
# ========================================================================

function Read-Utf8Text([string] $path) {
    # Windows PowerShell 5.1's `Get-Content -Raw` defaults to the system ANSI
    # code page, which mangles non-ASCII characters in CHANGELOG.md and
    # manifest.json (hearts, arrows, em dashes). Always read as UTF-8.
    return [System.IO.File]::ReadAllText(
        $path,
        [System.Text.UTF8Encoding]::new($false)
    )
}

function Get-LineEnding([string] $path) {
    # Preserve the file's existing line endings so we don't dirty every line
    # with a CRLF/LF flip.
    $bytes = [System.IO.File]::ReadAllBytes($path)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -eq 0x0A) {
            if ($i -gt 0 -and $bytes[$i - 1] -eq 0x0D) { return "`r`n" }
            return "`n"
        }
    }
    return [Environment]::NewLine
}

function Write-FilePreserving([string] $path, [string] $content, [string] $eol) {
    # Normalize to desired EOL and keep UTF-8 without BOM (matches existing files).
    $normalized = $content -replace "`r`n", "`n"
    if ($eol -eq "`r`n") { $normalized = $normalized -replace "`n", "`r`n" }
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($path, $normalized, $utf8NoBom)
}

function Test-VersionFormat([string] $v) {
    return $v -match '^\d+\.\d+\.\d+\.\d+$'
}

function Compare-Versions([string] $a, [string] $b) {
    return ([Version]$a).CompareTo([Version]$b)
}

function Test-StrictIsoDate([string] $d) {
    # ^\d{4}-\d{2}-\d{2}$ would let 9999-99-99 through. ParseExact rejects
    # impossible calendar dates.
    try {
        $null = [DateTime]::ParseExact(
            $d, 'yyyy-MM-dd',
            [System.Globalization.CultureInfo]::InvariantCulture
        )
        return $true
    } catch {
        return $false
    }
}

function Show-Change([string] $label, [string] $before, [string] $after) {
    Write-Host "  $label" -ForegroundColor Cyan
    Write-Host "    - $before" -ForegroundColor DarkRed
    Write-Host "    + $after"  -ForegroundColor DarkGreen
}

function Invoke-ShieldsJson([string] $url) {
    # Shields.io's `.json` endpoint returns {label, message, value, ...}.
    # Returns $null on any failure so callers can fall back to TBD.
    try {
        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10
        return ($resp.Content | ConvertFrom-Json)
    } catch {
        Write-Warning "Could not fetch $url : $($_.Exception.Message)"
        return $null
    }
}

function Get-CoveragePercent([string] $flag) {
    $url = "https://img.shields.io/codecov/c/github/JellyPlugins/jellyfin-helper.json?flag=$flag"
    $data = Invoke-ShieldsJson $url
    if (-not $data) { return $null }
    # Shields returns e.g. "94%". Normalize to an integer, ignore "unknown" etc.
    if ($data.value -match '^(\d+)%$') { return [int]$Matches[1] }
    return $null
}

function Get-E2eCountFromReadme([string] $readmeText) {
    # The e2e badge in README.md is a static shields.io badge, so its number
    # IS the source of truth after a release. Shape:
    #   badge/e2e%20tests-404-...
    $m = [regex]::Match($readmeText, 'badge/e2e%20tests-(\d+)-')
    if ($m.Success) { return [int]$m.Groups[1].Value }
    return $null
}

function Get-JellyfinMinorFromReadme([string] $readmeText) {
    # badge/Jellyfin-12.4+-... -> "12.4"
    $m = [regex]::Match($readmeText, 'badge/Jellyfin-([\d.]+)\+?-')
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

function Read-ChoiceSameOrNew(
    [string] $label,
    [string] $sameValue,
    [scriptblock] $validate
) {
    # Returns the chosen string. Loops until the answer is 'same' (case
    # insensitive) or a value passing $validate.
    while ($true) {
        Write-Host ""
        Write-Host "  $label" -ForegroundColor Yellow
        Write-Host "    Press Enter / type 'same' to keep: $sameValue"
        Write-Host "    Or type a new value:"
        $answer = Read-Host "  > "
        if (-not $answer -or $answer.Trim() -ieq 'same') { return $sameValue }
        $trimmed = $answer.Trim()
        if (& $validate $trimmed) { return $trimmed }
        Write-Host "    Invalid value - please try again." -ForegroundColor Red
    }
}

# ========================================================================
# Validate inputs
# ========================================================================

if (-not $PSScriptRoot) {
    throw "`$PSScriptRoot is empty - run this as a file (e.g. .\scripts\bump-version.ps1), not via dot-sourcing or Invoke-Expression."
}

if (-not (Test-VersionFormat $NewVersion)) {
    throw "NewVersion '$NewVersion' is not a 4-part version (x.x.x.x)."
}
if ($TargetAbi -and -not (Test-VersionFormat $TargetAbi)) {
    throw "TargetAbi '$TargetAbi' is not a 4-part version (x.x.x.x)."
}
if (-not $ReleaseDate) {
    $ReleaseDate = [DateTime]::UtcNow.ToString('yyyy-MM-dd', $script:Invariant)
}
if (-not (Test-StrictIsoDate $ReleaseDate)) {
    throw "ReleaseDate '$ReleaseDate' is not a valid YYYY-MM-DD calendar date."
}

$repo          = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$propsPath     = Join-Path $repo 'Directory.Build.props'
$buildYamlPath = Join-Path $repo 'build.yaml'
$manifestPath  = Join-Path $repo 'manifest.json'
$changelogPath = Join-Path $repo 'CHANGELOG.md'
$readmePath    = Join-Path $repo 'README.md'

foreach ($p in @($propsPath, $buildYamlPath, $manifestPath, $changelogPath)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Required file not found: $p" }
}

# ========================================================================
# Read everything up front - no writes yet
# ========================================================================

$propsEol      = Get-LineEnding $propsPath
$yamlEol       = Get-LineEnding $buildYamlPath
$manifestEol   = Get-LineEnding $manifestPath
$changelogEol  = Get-LineEnding $changelogPath

$propsText     = Read-Utf8Text $propsPath
$yamlText      = Read-Utf8Text $buildYamlPath
$manifestText  = Read-Utf8Text $manifestPath
$changelogText = Read-Utf8Text $changelogPath

# ----- Parse current versions from each source ---------------------------

$propsXml = [xml]$propsText
$propsVersionNode = $propsXml.SelectSingleNode('/Project/PropertyGroup/Version')
if (-not $propsVersionNode) { throw "No <Version> element found in Directory.Build.props." }
$propsVersion = $propsVersionNode.InnerText.Trim()

$yamlVersionMatch = [regex]::Match($yamlText, '(?m)^version:\s*"([^"]+)"\s*$')
if (-not $yamlVersionMatch.Success) { throw "No top-level `"version:`" line found in build.yaml." }
$yamlVersion = $yamlVersionMatch.Groups[1].Value

$manifestObj = $manifestText | ConvertFrom-Json
if (-not $manifestObj -or $manifestObj.Count -eq 0 -or
    -not $manifestObj[0].versions -or $manifestObj[0].versions.Count -eq 0) {
    throw "manifest.json has no versions array to read from."
}
$manifestTopVersion = $manifestObj[0].versions[0].version
$manifestTopAbi     = $manifestObj[0].versions[0].targetAbi

$changelogTopMatch = [regex]::Match($changelogText, '(?m)^##\s+\[(\d+\.\d+\.\d+\.\d+)\]')
if (-not $changelogTopMatch.Success) { throw "CHANGELOG.md has no '## [x.y.z.w]' heading." }
$changelogTopVersion = $changelogTopMatch.Groups[1].Value

# ----- Cross-file consistency check --------------------------------------

Write-Host ""
Write-Host "Current versions:" -ForegroundColor Yellow
Write-Host "  Directory.Build.props : $propsVersion"
Write-Host "  build.yaml            : $yamlVersion"
Write-Host "  manifest.json (top)   : $manifestTopVersion"
Write-Host "  CHANGELOG.md  (top)   : $changelogTopVersion"

$versions = @($propsVersion, $yamlVersion, $manifestTopVersion, $changelogTopVersion)
$uniqueVersions = @($versions | Sort-Object -Unique)

if ($uniqueVersions.Count -gt 1) {
    $msg = @(
        "Current versions are not consistent across the four files:",
        "  Directory.Build.props : $propsVersion",
        "  build.yaml            : $yamlVersion",
        "  manifest.json (top)   : $manifestTopVersion",
        "  CHANGELOG.md  (top)   : $changelogTopVersion",
        "",
        "Fix the drift by hand (align all four to the same version), then rerun this script."
    ) -join [Environment]::NewLine
    throw $msg
}

# All four versions are now guaranteed equal; use any of them as the baseline.
$currentVersion = $propsVersion
$cmp = Compare-Versions $NewVersion $currentVersion
if ($cmp -lt 0) {
    throw "NewVersion ($NewVersion) is lower than the current version ($currentVersion)."
}
if ((Compare-Versions $NewVersion $propsVersion) -eq 0) {
    Write-Warning "NewVersion matches the current version. Nothing to do."
    return
}

# ========================================================================
# Decide target ABI (prompt or default)
# ========================================================================

if (-not $TargetAbi) {
    if ($NonInteractive -or $DryRun) {
        $TargetAbi = $manifestTopAbi
        Write-Host ""
        Write-Host "Target ABI (non-interactive): $TargetAbi" -ForegroundColor Yellow
    }
    else {
        $TargetAbi = Read-ChoiceSameOrNew `
            -label "Target ABI for the new manifest entry" `
            -sameValue $manifestTopAbi `
            -validate { param($v) Test-VersionFormat $v }
    }
}

# ========================================================================
# Fetch badge values (optional)
# ========================================================================

$backendCoverage  = $null
$frontendCoverage = $null
$e2eCount         = $null
$jellyfinMinor    = $null

if (-not $NoFetch) {
    Write-Host ""
    Write-Host "Fetching live badge values..." -ForegroundColor Yellow
    $backendCoverage  = Get-CoveragePercent 'backend'
    $frontendCoverage = Get-CoveragePercent 'frontend'

    if (Test-Path -LiteralPath $readmePath) {
        $readmeText    = Read-Utf8Text $readmePath
        $e2eCount      = Get-E2eCountFromReadme     $readmeText
        $jellyfinMinor = Get-JellyfinMinorFromReadme $readmeText
    }

    Write-Host ("  backend coverage : {0}" -f $(if ($null -ne $backendCoverage)  { "$backendCoverage%"  } else { 'unknown (TBD)' }))
    Write-Host ("  frontend coverage: {0}" -f $(if ($null -ne $frontendCoverage) { "$frontendCoverage%" } else { 'unknown (TBD)' }))
    Write-Host ("  e2e tests        : {0}" -f $(if ($null -ne $e2eCount)         { "$e2eCount"         } else { 'unknown (TBD)' }))
    Write-Host ("  Jellyfin minimum : {0}" -f $(if ($jellyfinMinor)              { "$jellyfinMinor"    } else { 'unknown (TBD)' }))
}

function Format-Metric($value, [string] $suffix = '') {
    if ($null -ne $value -and "$value" -ne '') { return "$value$suffix" }
    return 'TBD'
}

$backendText  = Format-Metric $backendCoverage  '%'
$frontendText = Format-Metric $frontendCoverage '%'
$e2eText      = Format-Metric $e2eCount
$jfText       = Format-Metric $jellyfinMinor

# ========================================================================
# Plan the writes (header)
# ========================================================================

Write-Host ""
Write-Host "Bumping version -> $NewVersion" -ForegroundColor Yellow
Write-Host "Release date   : $ReleaseDate"
Write-Host "Target ABI     : $TargetAbi"
if ($DryRun) { Write-Host "Mode           : DRY RUN (no files will be written)" -ForegroundColor Yellow }

# These accumulate the final text per file; we only write at the end.
$plannedWrites = [ordered]@{}

# ========================================================================
# Stage 1: Directory.Build.props
# ========================================================================

Write-Host ""
Write-Host "Directory.Build.props" -ForegroundColor Green
$newProps = $propsText

foreach ($tag in 'Version','AssemblyVersion','FileVersion') {
    $node = $propsXml.SelectSingleNode("/Project/PropertyGroup/$tag")
    if (-not $node) { throw "No <$tag> in Directory.Build.props." }
    $oldVal = $node.InnerText.Trim()
    if ($oldVal -eq $NewVersion) {
        Write-Host "  <$tag> already $NewVersion - skipped" -ForegroundColor DarkGray
        continue
    }
    $pattern = "<$tag>$([regex]::Escape($oldVal))</$tag>"
    if ($newProps -notmatch $pattern) {
        throw "Could not find <$tag>$oldVal</$tag> in Directory.Build.props."
    }
    Show-Change "<$tag>" $oldVal $NewVersion
    $newProps = [regex]::Replace($newProps, $pattern, "<$tag>$NewVersion</$tag>")
}

if ($newProps -ne $propsText) {
    $plannedWrites[$propsPath] = @{ Text = $newProps; Eol = $propsEol }
}

# ========================================================================
# Stage 2: build.yaml
# ========================================================================

Write-Host ""
Write-Host "build.yaml" -ForegroundColor Green
$newYaml = $yamlText

# 2a) version line
if ($yamlVersion -ne $NewVersion) {
    $pattern = '(?m)^(version:\s*")' + [regex]::Escape($yamlVersion) + '(")\s*$'
    Show-Change 'version' $yamlVersion $NewVersion
    $newYaml = [regex]::Replace($newYaml, $pattern, "`${1}$NewVersion`${2}")
}

# 2b) changelog block - replace the entire 'changelog: |-' block.
#     Line-based approach: find the header line, then scan forward until we
#     hit the next top-level YAML key (line starting with non-whitespace) or
#     EOF. Avoids the (?ms)+dotall greedy-match bug that would swallow any
#     property added after changelog.
$headerMatch = [regex]::Match($newYaml, '(?m)^changelog:\s*\|-\s*\r?\n')
if (-not $headerMatch.Success) {
    throw "Could not find 'changelog: |-' header in build.yaml."
}
$blockStart    = $headerMatch.Index
$searchFrom    = $headerMatch.Index + $headerMatch.Length
$nextKeyMatch  = [regex]::Match($newYaml.Substring($searchFrom), '(?m)^\S')
$blockEnd      = if ($nextKeyMatch.Success) { $searchFrom + $nextKeyMatch.Index } else { $newYaml.Length }

$yamlScaffold = @(
    "changelog: |-"
    "  v$NewVersion - TBD short title"
    ""
    "  - TBD: summary bullet 1"
    "  - TBD: summary bullet 2"
    "  - Verified by $backendText backend unit test coverage, $frontendText UI test coverage, and $e2eText end-to-end Docker tests"
    "  - Requires Jellyfin $jfText - if you are on Jellyfin 10.x, stay on v2.1.0.6"
) -join "`n"

# Add a trailing newline so the next YAML key (if any) stays on its own line,
# or so the file ends cleanly at EOF.
$yamlScaffold += "`n"

$newYaml = $newYaml.Substring(0, $blockStart) + $yamlScaffold + $newYaml.Substring($blockEnd)

Show-Change 'changelog block' 'previous release notes' "TBD scaffold (coverage/e2e/jf prefilled where known)"

if ($newYaml -ne $yamlText) {
    $plannedWrites[$buildYamlPath] = @{ Text = $newYaml; Eol = $yamlEol }
}

# ========================================================================
# Stage 3: manifest.json
# ========================================================================

Write-Host ""
Write-Host "manifest.json" -ForegroundColor Green
$newManifest = $manifestText

if ($manifestTopVersion -eq $NewVersion) {
    Write-Warning "manifest.json already has an entry for $NewVersion - leaving it untouched."
}
else {
    $sourceUrl = "https://github.com/JellyPlugins/jellyfin-helper/releases/download/$NewVersion/JellyfinHelper_$NewVersion.zip"
    $timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', $script:Invariant)

    # Build the changelog as a JSON string literal (single-line with \n escapes
    # to match the shape of every existing entry in the file).
    $changelogJsonLiteral = `
        "v$NewVersion - TBD short title\n" + `
        "\n" + `
        "- TBD: summary bullet 1\n" + `
        "- TBD: summary bullet 2\n" + `
        "- Verified by $backendText backend unit test coverage, $frontendText UI test coverage, and $e2eText end-to-end Docker tests\n" + `
        "- Requires Jellyfin $jfText - if you are on Jellyfin 10.x, stay on v2.1.0.6"

    $newEntryLines = @(
        '      {'
        "        ""version"": ""$NewVersion"","
        "        ""changelog"": ""$changelogJsonLiteral"","
        "        ""targetAbi"": ""$TargetAbi"","
        "        ""sourceUrl"": ""$sourceUrl"","
        "        ""checksum"": ""TBD"","
        "        ""timestamp"": ""$timestamp"""
        '      },'
    )
    $newEntryBlock = ($newEntryLines -join "`n") + "`n"

    # Insert right after the opening of the versions array.
    $anchorPattern = '(?m)^(\s*"versions":\s*\[\s*\r?\n)'
    $m = [regex]::Match($newManifest, $anchorPattern)
    if (-not $m.Success) {
        throw "Could not find the '""versions"": [' anchor in manifest.json."
    }
    $insertAt = $m.Index + $m.Length
    $newManifest = $newManifest.Substring(0, $insertAt) +
                   $newEntryBlock +
                   $newManifest.Substring($insertAt)

    Show-Change 'versions[0].version'   $manifestTopVersion $NewVersion
    Show-Change 'versions[0].targetAbi' $manifestTopAbi     $TargetAbi
    Show-Change 'versions[0].sourceUrl' $manifestObj[0].versions[0].sourceUrl $sourceUrl
    Show-Change 'versions[0].checksum'  $manifestObj[0].versions[0].checksum  'TBD (fill in after ZIP build)'

    if ($newManifest -ne $manifestText) {
        $plannedWrites[$manifestPath] = @{ Text = $newManifest; Eol = $manifestEol }
    }
}

# ========================================================================
# Stage 4: CHANGELOG.md
# ========================================================================

Write-Host ""
Write-Host "CHANGELOG.md" -ForegroundColor Green
$newChangelog = $changelogText

$headerPattern = "(?m)^##\s+\[$([regex]::Escape($NewVersion))\]"
if ($newChangelog -match $headerPattern) {
    Write-Warning "CHANGELOG.md already has a section for $NewVersion - leaving it untouched."
}
else {
    $section = (@(
        "## [$NewVersion] - $ReleaseDate"
        ""
        "### Added"
        ""
        "- TBD"
        ""
        "### Changed"
        ""
        "- TBD"
        ""
        "### Fixed"
        ""
        "- TBD"
        ""
        "### Tests"
        ""
        "- Verified by $backendText backend unit test coverage, $frontendText UI test coverage, and $e2eText end-to-end Docker tests."
        ""
    ) -join "`n") + "`n"

    # Anchor only on versioned headings, not on '## [Unreleased]' or other
    # bracketed sections a future Keep-a-Changelog layout might introduce.
    $firstHeadingPattern = '(?m)^##\s+\[\d+\.\d+\.\d+\.\d+\]'
    $m = [regex]::Match($newChangelog, $firstHeadingPattern)
    if (-not $m.Success) {
        throw "CHANGELOG.md has no existing '## [x.y.z.w]' heading to anchor on."
    }
    $newChangelog = $newChangelog.Substring(0, $m.Index) + $section + $newChangelog.Substring($m.Index)

    Show-Change 'new section' '(none)' "## [$NewVersion] - $ReleaseDate (scaffold)"

    if ($newChangelog -ne $changelogText) {
        $plannedWrites[$changelogPath] = @{ Text = $newChangelog; Eol = $changelogEol }
    }
}

# ========================================================================
# Commit stage: write everything or write nothing
# ========================================================================

Write-Host ""
if ($plannedWrites.Count -eq 0) {
    Write-Host "No changes needed." -ForegroundColor Green
    return
}

if ($DryRun) {
    Write-Host "Dry run complete - would write $($plannedWrites.Count) file(s):" -ForegroundColor Yellow
    foreach ($p in $plannedWrites.Keys) { Write-Host "  $p" }
    return
}

foreach ($p in $plannedWrites.Keys) {
    $entry = $plannedWrites[$p]
    Write-FilePreserving $p $entry.Text $entry.Eol
}

Write-Host "Version bumped to $NewVersion. Wrote $($plannedWrites.Count) file(s)." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Fill in the 'TBD' placeholders in CHANGELOG.md, build.yaml, manifest.json."
Write-Host "  2. Build the release ZIP, then replace 'checksum: TBD' in manifest.json."
Write-Host "  3. Review:  git diff -- Directory.Build.props build.yaml manifest.json CHANGELOG.md"
Write-Host "  4. Commit:  git commit -am ""Bump version to $NewVersion"""
Write-Host ""
Write-Host "Grep for leftovers before releasing:" -ForegroundColor Yellow
Write-Host "  git grep -n 'TBD' -- CHANGELOG.md build.yaml manifest.json"