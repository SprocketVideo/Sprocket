#!/usr/bin/env pwsh
#
# Sprocket change-overview generator (extracted from gh-release.ps1 when release builds moved to CI —
# .github/workflows/release.yml runs this to build the release body).
#
# Builds a high-level markdown "What's changed since <previous tag>" overview: the commit subjects
# between the previous v* tag and -Tag (default HEAD), grouped by conventional-commit type into
# friendly sections. "chore: release ..." commits are dropped as noise. Prints to stdout (empty
# output when there is nothing to report), or writes to -OutFile.
#
#   pwsh scripts/changelog.ps1                        # overview for HEAD since the last v* tag
#   pwsh scripts/changelog.ps1 -Tag v0.1.55-alpha.1   # overview for a tag since its predecessor
#   pwsh scripts/changelog.ps1 -OutFile overview.md
#
# Needs full git history (CI: actions/checkout with fetch-depth: 0) so the previous tag is visible.

[CmdletBinding()]
param(
    # The release commit/tag the overview describes. The previous release is the most recent v* tag
    # reachable from its parent.
    [string] $Tag = 'HEAD',

    # Write the overview here instead of stdout (still empty file when nothing to report).
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# Build a high-level markdown overview of what changed since $sinceTag: the commit subjects in
# ($sinceTag..$Tag), grouped by conventional-commit type into friendly sections. Returns $null when
# there is nothing to report.
function Get-ChangeOverview([string] $sinceTag) {
    $range = if ($sinceTag) { "$sinceTag..$Tag" } else { $Tag }
    $raw = & git -C $repoRoot log $range --no-merges --pretty=format:'%s'
    $subjects = @($raw | Where-Object { $_ -and ($_ -notmatch '^chore(\([^)]*\))?:\s*release\b') })
    if (-not $subjects) { return $null }

    # Conventional-commit type -> section title, in display order. Unrecognized / unprefixed
    # subjects fall into "Other".
    $sections = [ordered]@{
        feat     = 'Features'
        fix      = 'Fixes'
        perf     = 'Performance'
        refactor = 'Refactoring'
        docs     = 'Documentation'
        build    = 'Build'
        ci       = 'CI'
        test     = 'Tests'
        style    = 'Style'
        chore    = 'Chores'
        other    = 'Other'
    }
    $buckets = @{}
    foreach ($key in $sections.Keys) { $buckets[$key] = [System.Collections.Generic.List[string]]::new() }

    foreach ($s in $subjects) {
        if ($s -match '^(?<type>\w+)(?<scope>\([^)]*\))?!?:\s*(?<desc>.+)$') {
            $type = $Matches['type'].ToLowerInvariant()
            if ($type -eq 'tests') { $type = 'test' }
            if (-not $buckets.ContainsKey($type)) { $type = 'other' }
            $scope = if ($Matches['scope']) { ($Matches['scope'] -replace '[()]', '') } else { '' }
            $desc = $Matches['desc'].Trim()
            $bullet = if ($scope) { "**${scope}:** $desc" } else { $desc }
            $buckets[$type].Add($bullet)
        }
        else {
            $buckets['other'].Add($s.Trim())
        }
    }

    $count = $subjects.Count
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add($(if ($sinceTag) { "## What's changed since $sinceTag" } else { "## What's changed" }))
    $lines.Add('')
    $lines.Add("_$count commit$(if ($count -ne 1) { 's' }) in this release._")
    $lines.Add('')
    foreach ($key in $sections.Keys) {
        if ($buckets[$key].Count -eq 0) { continue }
        $lines.Add("### $($sections[$key])")
        foreach ($item in $buckets[$key]) { $lines.Add("- $item") }
        $lines.Add('')
    }
    return ($lines -join "`n").TrimEnd()
}

# The previous release is the most recent v* tag reachable from the release commit's PARENT — so a
# tag on HEAD describes itself against its predecessor, not against itself.
$prevTag = (& git -C $repoRoot describe --tags --abbrev=0 --match 'v*' "$Tag^" 2>$null)
if ($LASTEXITCODE -ne 0) { $prevTag = '' }
$prevTag = "$prevTag".Trim()
$global:LASTEXITCODE = 0   # an (expected) describe miss must not fail the caller

$overview = Get-ChangeOverview $prevTag
if ($OutFile) {
    Set-Content -Path $OutFile -Value $(if ($overview) { "$overview`n" } else { '' }) -Encoding utf8 -NoNewline
}
elseif ($overview) {
    Write-Output $overview
}
