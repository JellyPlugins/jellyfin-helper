<#
.SYNOPSIS
    Collects merged pull requests between the last release tag and HEAD into a
    Markdown draft, intended as input for hand-writing (or LLM-assisting) the
    next CHANGELOG section.

.DESCRIPTION
    Walks git history in the given range, extracts PR numbers from merge-commit
    subjects, then queries the public GitHub REST API for each PR to pull its
    title, author, labels, merge date, size, and body.

    GitHub API rate limit without a token is 60 req/h/IP. Set $env:GITHUB_TOKEN
    (or pass -Token) to lift it to 5000 req/h. The script prints the remaining
    quota after its last request so you can tell whether a rerun is safe.

    The draft file is intentionally not committed (see .gitignore).

.PARAMETER SinceTag
    Starting tag or ref. Defaults to the latest tag reachable from HEAD
    (`git describe --tags --abbrev=0`).

.PARAMETER UntilRef
    Ending ref. Defaults to HEAD.

.PARAMETER Output
    Output markdown path. Defaults to <repo-root>/release-notes-draft.md.

.PARAMETER Repo
    GitHub `owner/name` slug. Defaults to JellyPlugins/jellyfin-helper.

.PARAMETER Token
    GitHub token. Defaults to $env:GITHUB_TOKEN, else unauthenticated.

.PARAMETER NoFetch
    Skip GitHub API calls. Produces a draft with commit-level data only.

.EXAMPLE
    pwsh scripts/collect-release-notes.ps1

.EXAMPLE
    pwsh scripts/collect-release-notes.ps1 -SinceTag 3.0.1.4 -Token $env:GITHUB_TOKEN
#>

[CmdletBinding()]
param(
    [string] $SinceTag,
    [string] $UntilRef = 'HEAD',
    [string] $Output,
    [string] $Repo = 'JellyPlugins/jellyfin-helper',
    [string] $Token,
    [switch] $NoFetch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ========================================================================
# Helpers
# ========================================================================

function Write-Utf8NoBom([string] $path, [string] $content) {
    $dir = Split-Path -Parent $path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($path, $content, $utf8NoBom)
}

function Invoke-Git([string[]] $GitArgs) {
    # git writes some normal output to stderr (e.g. "warning:"). We want stdout.
    $out = & git @GitArgs 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "git $($GitArgs -join ' ') failed with exit code $LASTEXITCODE."
    }
    return $out
}

function Get-RepoRoot {
    if (-not $PSScriptRoot) {
        throw "`$PSScriptRoot is empty - run this as a file, not via dot-sourcing."
    }
    return (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Resolve-SinceTag([string] $explicit) {
    if ($explicit) { return $explicit }
    try {
        $tag = (Invoke-Git @('describe', '--tags', '--abbrev=0')).Trim()
        if (-not $tag) { throw "no tag" }
        return $tag
    } catch {
        throw "No -SinceTag given and no reachable tag found. Pass -SinceTag explicitly."
    }
}

function Test-Ref([string] $ref) {
    & git rev-parse --verify --quiet "$ref^{commit}" 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function Get-RefDate([string] $ref) {
    try {
        return (Invoke-Git @('log', '-1', '--format=%cI', $ref)).Trim()
    } catch { return '' }
}

# PR numbers typically appear as:
#   - "Some fix (#123)"                              (squash merge)
#   - "Merge pull request #123 from user/branch"     (merge commit)
# We take the first match per commit subject so a reference to another PR in
# the body doesn't double-count it.
$script:PrNumberRegex = '(?:(?:Merge pull request|pull/)\s*#?|\(#)(\d+)\)?'

function Get-CommitsInRange([string] $from, [string] $to) {
    $raw = Invoke-Git @('log', "$from..$to", '--pretty=format:%H%x1f%cI%x1f%an%x1f%s')
    if (-not $raw) { return @() }
    $commits = @()
    foreach ($line in ($raw -split "`r?`n")) {
        if (-not $line) { continue }
        $parts = $line.Split([char]0x1f)
        if ($parts.Count -lt 4) { continue }
        $sha      = $parts[0]
        $date     = $parts[1]
        $author   = $parts[2]
        $subject  = $parts[3]
        $prMatch  = [regex]::Match($subject, $script:PrNumberRegex)
        $prNumber = if ($prMatch.Success) { [int]$prMatch.Groups[1].Value } else { $null }
        $commits += [pscustomobject]@{
            Sha      = $sha
            ShortSha = $sha.Substring(0, 7)
            Date     = $date
            Author   = $author
            Subject  = $subject
            PrNumber = $prNumber
        }
    }
    return ,$commits
}

function Invoke-GitHubApi([string] $path, [string] $tok) {
    $url = "https://api.github.com$path"
    $headers = @{
        'Accept'               = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent'           = 'jellyfin-helper-release-notes-script'
    }
    if ($tok) { $headers['Authorization'] = "Bearer $tok" }

    try {
        $resp = Invoke-WebRequest -Uri $url -Headers $headers -UseBasicParsing -TimeoutSec 20
    } catch {
        $r = $_.Exception.Response
        if ($r) {
            $status = [int]$r.StatusCode
            if ($status -eq 403) {
                $remaining = $r.Headers['X-RateLimit-Remaining']
                if ($remaining -eq '0') {
                    $reset = $r.Headers['X-RateLimit-Reset']
                    throw "GitHub API rate limit exceeded. Resets at Unix time $reset. Set `$env:GITHUB_TOKEN to lift the limit to 5000/h."
                }
            }
            if ($status -eq 404) {
                return $null  # Not a real PR number (might be an issue/discussion)
            }
            throw "GitHub API $url returned HTTP $status."
        }
        throw
    }

    $remaining = $resp.Headers['X-RateLimit-Remaining']
    $limit     = $resp.Headers['X-RateLimit-Limit']
    $script:LastRateInfo = @{ Remaining = $remaining; Limit = $limit }
    return ($resp.Content | ConvertFrom-Json)
}

function Get-PullRequest([string] $repo, [int] $number, [string] $tok) {
    return Invoke-GitHubApi "/repos/$repo/pulls/$number" $tok
}

function Trim-Body([string] $body, [int] $maxLines = 15) {
    if (-not $body) { return '_(no description)_' }
    # Strip HTML comments (PR templates often contain <!-- ... -->).
    $clean = [regex]::Replace($body, '(?s)<!--.*?-->', '').Trim()
    if (-not $clean) { return '_(no description)_' }
    $lines = $clean -split "`r?`n"
    if ($lines.Count -le $maxLines) { return $clean }
    return ((@($lines[0..($maxLines - 1)]) -join "`n") + "`n`n_(truncated - see PR link for full description)_")
}

function Format-Date([string] $iso) {
    if (-not $iso) { return '' }
    try { return ([DateTime]::Parse($iso)).ToString('yyyy-MM-dd') } catch { return $iso }
}

# ========================================================================
# Resolve inputs
# ========================================================================

$repoRoot = Get-RepoRoot
Push-Location $repoRoot
try {
    $SinceTag = Resolve-SinceTag $SinceTag
    if (-not (Test-Ref $SinceTag))  { throw "SinceTag '$SinceTag' is not a valid git ref." }
    if (-not (Test-Ref $UntilRef))  { throw "UntilRef '$UntilRef' is not a valid git ref." }

    if (-not $Token) { $Token = $env:GITHUB_TOKEN }

    if (-not $Output) { $Output = Join-Path $repoRoot 'release-notes-draft.md' }

    $sinceDate = Format-Date (Get-RefDate $SinceTag)
    $untilDate = Format-Date (Get-RefDate $UntilRef)

    Write-Host ""
    Write-Host "Release notes draft" -ForegroundColor Yellow
    Write-Host "  Repo      : $Repo"
    Write-Host "  Range     : $SinceTag ($sinceDate) -> $UntilRef ($untilDate)"
    Write-Host "  Output    : $Output"
    Write-Host "  GitHub    : $(if ($NoFetch) { 'skipped (-NoFetch)' } elseif ($Token) { 'authenticated (5000/h)' } else { 'unauthenticated (60/h)' })"

    # ====================================================================
    # Collect commits + PR numbers
    # ====================================================================

    $commits = Get-CommitsInRange $SinceTag $UntilRef
    $prNumbers = @(
        $commits |
            Where-Object { $_.PrNumber } |
            Select-Object -ExpandProperty PrNumber -Unique |
            Sort-Object
    )
    $orphanCommits = @($commits | Where-Object { -not $_.PrNumber })

    Write-Host ""
    Write-Host "  Commits   : $($commits.Count)"
    Write-Host "  PRs found : $($prNumbers.Count)"
    Write-Host "  Orphans   : $($orphanCommits.Count) (commits without a #NNN reference)"

    # ====================================================================
    # Fetch PR details from GitHub
    # ====================================================================

    $prs = @()
    $script:LastRateInfo = $null
    if (-not $NoFetch -and $prNumbers.Count -gt 0) {
        Write-Host ""
        Write-Host "Fetching PR metadata..." -ForegroundColor Yellow
        $i = 0
        foreach ($num in $prNumbers) {
            $i++
            Write-Host ("  [{0}/{1}] #{2}" -f $i, $prNumbers.Count, $num) -NoNewline
            try {
                $pr = Get-PullRequest $Repo $num $Token
            } catch {
                Write-Host " - FAILED: $($_.Exception.Message)" -ForegroundColor Red
                throw
            }
            if ($null -eq $pr) {
                Write-Host " - not a PR (skipped)" -ForegroundColor DarkGray
                continue
            }
            Write-Host " - $($pr.title)"
            $prs += $pr
        }
        if ($script:LastRateInfo) {
            Write-Host ""
            Write-Host ("Rate limit after run: {0} of {1} remaining." -f $script:LastRateInfo.Remaining, $script:LastRateInfo.Limit)
        }
    }

    # ====================================================================
    # Build the markdown
    # ====================================================================

    $out = [System.Text.StringBuilder]::new()
    [void]$out.AppendLine("# Release notes draft")
    [void]$out.AppendLine()
    [void]$out.AppendLine("**Repo**: ``$Repo``  ")
    [void]$out.AppendLine("**Range**: ``$SinceTag`` ($sinceDate) -> ``$UntilRef`` ($untilDate)  ")
    [void]$out.AppendLine("**Commits**: $($commits.Count) - **PRs**: $($prNumbers.Count) - **Orphans**: $($orphanCommits.Count)")
    [void]$out.AppendLine()
    [void]$out.AppendLine("> Generated $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm')) UTC by ``scripts/collect-release-notes.ps1``.")
    [void]$out.AppendLine("> Draft only. This file is in ``.gitignore`` and must not be committed.")
    [void]$out.AppendLine()
    [void]$out.AppendLine("---")
    [void]$out.AppendLine()

    # ---- PR section ----
    [void]$out.AppendLine("## Merged pull requests")
    [void]$out.AppendLine()
    if ($prs.Count -eq 0) {
        if ($NoFetch) {
            [void]$out.AppendLine("_PR metadata fetch was skipped (``-NoFetch``). PR numbers found in commits:_")
            [void]$out.AppendLine()
            foreach ($num in $prNumbers) {
                [void]$out.AppendLine("- #$num - https://github.com/$Repo/pull/$num")
            }
            [void]$out.AppendLine()
        } else {
            [void]$out.AppendLine("_No merged PRs found in this range._")
            [void]$out.AppendLine()
        }
    } else {
        # Sort PRs by merged_at ascending so the changelog reads chronologically.
        $sortedPrs = $prs | Sort-Object { [DateTime]::Parse($_.merged_at) }
        foreach ($pr in $sortedPrs) {
            $labels = @()
            if ($pr.labels) { $labels = @($pr.labels | ForEach-Object { $_.name }) }
            $labelText = if ($labels.Count -gt 0) { ($labels | ForEach-Object { "``$_``" }) -join ', ' } else { '_(none)_' }

            [void]$out.AppendLine("### #$($pr.number) - $($pr.title)")
            [void]$out.AppendLine()
            [void]$out.AppendLine("- **Author**: @$($pr.user.login)")
            [void]$out.AppendLine("- **Merged**: $(Format-Date $pr.merged_at)")
            [void]$out.AppendLine("- **Labels**: $labelText")
            [void]$out.AppendLine("- **Link**: $($pr.html_url)")
            [void]$out.AppendLine("- **Size**: +$($pr.additions) / -$($pr.deletions) across $($pr.changed_files) file(s)")
            [void]$out.AppendLine()
            [void]$out.AppendLine("<details><summary>PR description</summary>")
            [void]$out.AppendLine()
            [void]$out.AppendLine((Trim-Body $pr.body))
            [void]$out.AppendLine()
            [void]$out.AppendLine("</details>")
            [void]$out.AppendLine()
            [void]$out.AppendLine("---")
            [void]$out.AppendLine()
        }
    }

    # ---- Orphan commits section ----
    if ($orphanCommits.Count -gt 0) {
        [void]$out.AppendLine("## Commits without an associated PR")
        [void]$out.AppendLine()
        [void]$out.AppendLine("_These went in via direct push or had their PR number stripped from the subject. Review them manually; usually fine to ignore for release notes, but worth a glance for anything user-visible._")
        [void]$out.AppendLine()
        foreach ($c in $orphanCommits) {
            [void]$out.AppendLine("- ``$($c.ShortSha)`` $($c.Subject) - *$($c.Author)* ($(Format-Date $c.Date))")
        }
        [void]$out.AppendLine()
        [void]$out.AppendLine("---")
        [void]$out.AppendLine()
    }

    # ---- LLM prompt ----
    [void]$out.AppendLine("## Suggested prompt")
    [void]$out.AppendLine()
    [void]$out.AppendLine("Paste the sections above into your LLM of choice together with this prompt:")
    [void]$out.AppendLine()
    [void]$out.AppendLine("> Convert the merged PRs above into a CHANGELOG section matching the style of recent entries in ``CHANGELOG.md`` (paste a couple as examples). Rules:")
    [void]$out.AppendLine("> ")
    [void]$out.AppendLine("> - User-facing voice, not code-facing (describe what the user sees/does, not which file changed).")
    [void]$out.AppendLine("> - Group into **Breaking / Added / Changed / Fixed / Tests** per Keep-a-Changelog.")
    [void]$out.AppendLine("> - Skip pure refactors, dependency bumps, and test-only PRs unless they introduce a new user-visible test surface.")
    [void]$out.AppendLine("> - Keep bullets terse (one or two sentences). Lead with the outcome.")
    [void]$out.AppendLine("> - Preserve the ``Verified by X% backend coverage, Y% UI coverage, Z end-to-end Docker tests`` trailer from the scaffold in ``CHANGELOG.md``.")
    [void]$out.AppendLine()

    Write-Utf8NoBom $Output $out.ToString()

    Write-Host ""
    Write-Host "Draft written to:" -ForegroundColor Green
    Write-Host "  $Output"
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Yellow
    Write-Host "  1. Open the draft and skim the PR list. Delete anything you don't want to mention."
    Write-Host "  2. Feed the remaining PRs + the prompt at the bottom into your LLM."
    Write-Host "  3. Paste the output into the new CHANGELOG.md section created by bump-version.ps1."
}
finally {
    Pop-Location
}
