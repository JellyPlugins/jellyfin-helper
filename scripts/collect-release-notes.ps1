<#
.SYNOPSIS
    Collects merged pull requests between the last release tag and HEAD into a
    Markdown draft, intended as input for hand-writing (or LLM-assisting) the
    next CHANGELOG section.

.DESCRIPTION
    Walks git history in the given range, extracts PR numbers from merge-commit
    subjects, then queries the public GitHub REST API for each PR to pull its
    title, author, labels, merge date, size, and body.

    The first commit after SinceTag is always excluded, since it is usually
    a bot-generated checksum or release-metadata update.

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

# InvariantCulture pins format separators and the Gregorian calendar, so
# DateTime.ToString('HH:mm') emits "10:30" on every host (not "10.30" on
# da-DK) and "yyyy" emits the Gregorian year (not the Buddhist year on
# th-TH). Every DateTime.ToString / .Parse below must use it.
$script:Invariant = [System.Globalization.CultureInfo]::InvariantCulture

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
#   - "Some fix (#123)"                              (squash merge, trailing)
#   - "Merge pull request #123 from user/branch"     (merge commit, leading)
# Anchoring (?:\(#N\)\s*$) and (?:^Merge pull request #N) keeps the match on
# the real PR number even when the title mentions another PR, e.g.
# 'Revert "Add x (#45)" (#46)' - a plain first-match would pick #45.
$script:PrNumberRegex = '(?:\(#(\d+)\)\s*$|^Merge pull request\s+#(\d+))'

function Get-CommitsInRange([string] $from, [string] $to) {
    # --first-parent keeps one entry per merge in a merge-commit workflow
    # (otherwise every branch commit shows up as an orphan). Safe on a
    # squash-merge workflow: linear history already has only first-parents.
    $raw = Invoke-Git @('log', '--first-parent', "$from..$to", '--pretty=format:%H%x1f%cI%x1f%an%x1f%s')
    # `return ,@()` wraps the empty array so the pipeline does not unroll
    # it to $null (strict mode then rejects $commits.Count in the caller).
    if (-not $raw) { return ,@() }
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
        $prNumber = $null
        if ($prMatch.Success) {
            # Two alternation branches, one capturing group each; read
            # whichever actually matched.
            $g = if ($prMatch.Groups[1].Success) { $prMatch.Groups[1] } else { $prMatch.Groups[2] }
            $prNumber = [int]$g.Value
        }
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

function Get-HeaderValue($headers, [string] $name) {
    # PS 5.1 wraps headers as a case-insensitive Dictionary[string,string[]]
    # where indexing returns the value directly. PS 7+ sometimes surfaces
    # HttpResponseHeaders where [] can throw. Try both shapes, return null
    # when the header is absent rather than letting strict mode throw.
    if ($null -eq $headers) { return $null }
    try {
        if ($headers -is [System.Collections.IDictionary]) {
            if ($headers.Contains($name)) {
                $v = $headers[$name]
                if ($v -is [array]) { return ($v | Select-Object -First 1) }
                return $v
            }
            return $null
        }
        # Fallback: .NET HttpHeaders style (PS 7+ Invoke-WebRequest).
        $tryGet = $headers.GetType().GetMethod('TryGetValues')
        if ($tryGet) {
            # Reflection cannot bind a PowerShell [ref] to a .NET `out`
            # parameter: the second slot has to be a plain object the method
            # writes back into. $invokeArgs is also *not* named $args - that
            # name collides with PowerShell's automatic $Args parameter and
            # the splat would silently misbehave (same bug class as $GitArgs
            # higher up in this file).
            $invokeArgs = @($name, $null)
            $ok = $tryGet.Invoke($headers, $invokeArgs)
            if ($ok -and $invokeArgs[1]) {
                return (@($invokeArgs[1]) | Select-Object -First 1)
            }
            return $null
        }
        return $headers[$name]
    } catch {
        return $null
    }
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
        # Strict mode: never dot into .Response without checking - timeouts and
        # DNS failures raise exceptions with no Response property at all, and
        # the real error message would be lost to a PropertyNotFoundException.
        $exn = $_.Exception
        $r = if ($exn.PSObject.Properties['Response']) { $exn.Response } else { $null }
        if ($r) {
            $status = [int]$r.StatusCode
            if ($status -eq 403) {
                $remaining = Get-HeaderValue $r.Headers 'X-RateLimit-Remaining'
                if ($remaining -eq '0') {
                    $reset = Get-HeaderValue $r.Headers 'X-RateLimit-Reset'
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

    $remaining = Get-HeaderValue $resp.Headers 'X-RateLimit-Remaining'
    $limit     = Get-HeaderValue $resp.Headers 'X-RateLimit-Limit'
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
    try {
        return ([DateTime]::Parse($iso, $script:Invariant)).ToString('yyyy-MM-dd', $script:Invariant)
    } catch { return $iso }
}

# ========================================================================
# Resolve inputs
# ========================================================================

$repoRoot = Get-RepoRoot

# Resolve -Output against the caller's cwd BEFORE Push-Location runs: the push
# changes $PWD but not [System.IO.Directory]::GetCurrentDirectory(), which is
# what [System.IO.File]::WriteAllText uses for relative paths. A user running
# `scripts\collect-release-notes.ps1 -Output out\draft.md` from somewhere
# outside the repo would otherwise see Test-Path/New-Item resolve against
# the repo root while the actual file write resolves somewhere else.
if ($Output) {
    $Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
}

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
    $skippedCommit = $null
    if ($commits.Count -gt 0) {
        # git log returns newest first, so the last entry is the first commit
        # after the release. Exclude it before collecting PRs or orphans.
        $skippedCommit = $commits[-1]
        $commits = @($commits | Select-Object -SkipLast 1)
    }
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
    if ($skippedCommit) {
        Write-Host "  Skipped   : $($skippedCommit.ShortSha) $($skippedCommit.Subject) (first commit after $SinceTag)" -ForegroundColor DarkGray
    }

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
            # Revert commits can reference open or closed-unmerged PRs. Those
            # return merged_at = null, and [DateTime]::Parse($null) later would
            # throw after every API call has already run, losing the whole run.
            if (-not $pr.merged_at) {
                Write-Host " - not merged (skipped)" -ForegroundColor DarkGray
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
    [void]$out.AppendLine("> Generated $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm', $script:Invariant)) UTC by ``scripts/collect-release-notes.ps1``.")
    [void]$out.AppendLine("> Draft only. This file is in ``.gitignore`` and must not be committed.")
    if ($skippedCommit) {
        [void]$out.AppendLine("> The first commit after ``$SinceTag`` was excluded (usually bot-generated release metadata).")
    }
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
        $sortedPrs = $prs | Sort-Object { [DateTime]::Parse($_.merged_at, $script:Invariant) }
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
