$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$docsRoot = Join-Path $repoRoot 'docs'
$cssTarget = Join-Path $docsRoot 'css'
$jsTarget = Join-Path $docsRoot 'js'
$i18nTarget = Join-Path $docsRoot 'i18n'

$generatedTargets = @($cssTarget, $jsTarget, $i18nTarget)
foreach ($target in $generatedTargets) {
    if (Test-Path $target) {
        Remove-Item $target -Recurse -Force
    }

    New-Item -ItemType Directory -Path $target -Force | Out-Null
}

Copy-Item (Join-Path $repoRoot 'Jellyfin.Plugin.JellyfinHelper\PluginPages\css\*.css') $cssTarget -Force
Copy-Item (Join-Path $repoRoot 'Jellyfin.Plugin.JellyfinHelper\PluginPages\js\*.js') $jsTarget -Force
Copy-Item (Join-Path $repoRoot 'Jellyfin.Plugin.JellyfinHelper\i18n\en.json') $i18nTarget -Force

$noJekyllPath = Join-Path $docsRoot '.nojekyll'
Set-Content -Path $noJekyllPath -Value '' -NoNewline

# Remove docs/.gitignore so the deploy action does NOT skip generated css/js/i18n folders
$docsGitignore = Join-Path $docsRoot '.gitignore'
if (Test-Path $docsGitignore) {
    Remove-Item $docsGitignore -Force
    Write-Host "Removed $docsGitignore (would hide generated assets from deploy)"
}

Write-Host 'Demo site prepared:'
Get-ChildItem -Path $docsRoot -Recurse -File |
    Sort-Object FullName |
    ForEach-Object { Write-Host $_.FullName.Substring($repoRoot.Path.Length + 1) }


