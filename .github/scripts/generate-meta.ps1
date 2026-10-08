# Generate meta.json for Jellyfin Helper Plugin Used by deploy-to-truenas.bat to create identical meta.json as the release pipeline Reads version and changelog directly from manifest.json to avoid CMD escaping issues.
param(
    [Parameter(Mandatory)][string]$OutputDir
)

$ErrorActionPreference = 'Stop'

$manifest = Get-Content 'manifest.json' | ConvertFrom-Json
$version = $manifest[0].versions[0].version
$changelog = $manifest[0].versions[0].changelog
$targetAbi = $manifest[0].versions[0].targetAbi
$description = $manifest[0].description
$overview = $manifest[0].overview

$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.0000000Z")

$meta = [ordered]@{
    category    = "General"
    changelog   = $changelog
    description = $description
    guid        = "0c737645-5cbb-4bd8-80c7-d377b560aaa4"
    name        = "Jellyfin Helper"
    overview    = $overview
    owner       = "JellyPlugins"
    targetAbi   = $targetAbi
    timestamp   = $timestamp
    version     = $version
    status      = "Active"
    autoUpdate  = $true
    imageUrl    = "https://raw.githubusercontent.com/JellyPlugins/jellyfin-helper/main/media/logo.png"
    imagePath   = "logo.png"
    assemblies  = @()
}

# Write UTF-8 WITHOUT a BOM. Set-Content -Encoding UTF8NoBOM only exists on
# PowerShell 6+, and Windows PowerShell 5.1's -Encoding UTF8 emits a BOM (which
# Jellyfin's JSON reader can reject). Use the .NET writer for BOM-less output on
# every PowerShell version. Resolve to an absolute path first, since the .NET
# writer uses the process working directory, not PowerShell's location.
$json = $meta | ConvertTo-Json -Depth 10
$outFile = Join-Path (Resolve-Path $OutputDir) "meta.json"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($outFile, $json, $utf8NoBom)

Write-Host "  Version:   $version"
Write-Host "  Changelog: $changelog"