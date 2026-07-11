#!/usr/bin/env pwsh
#
# Sprocket release tag-cutter.
#
# Cuts a release by version-bumping, committing, tagging, and pushing — the heavy lifting (per-OS
# builds, Velopack packaging, smoke tests, GitHub release creation with all assets) happens in CI:
# pushing the v* tag triggers .github/workflows/release.yml, which builds each OS on its native
# runner (the only way to produce the macOS .app at all) and assembles the release. This script then
# watches that run.
#
#   pwsh scripts/gh-release.ps1                      # bump patch, tag v<ver>-alpha, push, watch CI
#   pwsh scripts/gh-release.ps1 -DryRun              # show every step without changing git or GitHub
#   pwsh scripts/gh-release.ps1 -NoBump              # release the current Directory.Build.props version as-is
#   pwsh scripts/gh-release.ps1 -Tag v0.2.0-alpha    # use an exact git tag / release name
#   pwsh scripts/gh-release.ps1 -PreReleaseLabel beta            # tag v<ver>-beta
#   pwsh scripts/gh-release.ps1 -NotPreRelease      # a full release: no suffix, not marked prerelease
#
# Release notes: the CI release job builds the body — an auto "What's changed since <prev tag>"
# overview (scripts/changelog.ps1, grouped by conventional-commit type) on top of the evergreen
# RELEASE_NOTES.md preamble. RELEASE_NOTES.md is version-agnostic on purpose — don't put a version
# number or a "what works / not yet" feature list in it; those drift. Full status lives in PLAN.md.
#
# Versioning: the X.Y.Z version lives in Directory.Build.props (<VersionPrefix>) as the single source
# of truth; CI verifies the tag matches it before building. The GIT TAG carries the prerelease suffix
# (e.g. v0.1.64-alpha); CI stamps that suffix into the binaries' InformationalVersion.
#
# Local builds are still possible without CI: scripts/release.ps1 does everything for the RIDs the
# host can build (macOS Velopack packs require a Mac).
#
# Requires: gh (authenticated — run `gh auth login`) and a clean working tree.

[CmdletBinding()]
param(
    # Exact git tag / GitHub release name. When omitted it is derived from the version as
    # "v<version>-<PreReleaseLabel>".
    [string] $Tag,

    # Prerelease suffix used when -Tag is not given (e.g. alpha, beta, rc.1).
    [string] $PreReleaseLabel = 'alpha',

    # Release the current Directory.Build.props version without bumping the patch.
    [switch] $NoBump,

    # A full release: the tag carries no prerelease suffix and CI does not mark it prerelease.
    [switch] $NotPreRelease,

    # Show every action without mutating git or GitHub.
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$propsFile = Join-Path $repoRoot 'Directory.Build.props'

function Write-Step([string] $msg) { Write-Host "==> $msg" -ForegroundColor Green }
function Write-Info([string] $msg) { Write-Host "    $msg" }

function Get-BaseVersion {
    $content = Get-Content $propsFile -Raw
    if ($content -match '<VersionPrefix>\s*([^<\s]+)\s*</VersionPrefix>') { return $Matches[1] }
    throw "No <VersionPrefix> found in $propsFile"
}

function Set-BaseVersion([string] $v) {
    $content = Get-Content $propsFile -Raw
    $updated = [regex]::Replace($content, '(<VersionPrefix>)\s*[^<\s]+\s*(</VersionPrefix>)', "`${1}$v`${2}")
    Set-Content -Path $propsFile -Value $updated -NoNewline
}

# ---- Preflight -------------------------------------------------------------------------------
Write-Step 'Preflight checks'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is not on PATH. Install it and run 'gh auth login'."
}
& gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "gh is not authenticated. Run 'gh auth login' first." }
Write-Info 'gh authenticated.'

# Working tree must be clean: the version bump must be the only change the release commit carries.
$dirty = & git -C $repoRoot status --porcelain
if ($dirty) {
    Write-Host $dirty
    throw "Working tree is not clean. Commit or stash changes before cutting a release."
}
$branch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
Write-Info "Clean tree on branch '$branch'."

# ---- Version bump ----------------------------------------------------------------------------
$base = Get-BaseVersion
if ($NoBump) {
    $version = $base
    Write-Info "Version: $version (no bump)"
}
else {
    $parts = $base.Split('.')
    if ($parts.Count -lt 3) { throw "VersionPrefix '$base' is not in X.Y.Z form." }
    $parts[2] = [string]([int]$parts[2] + 1)
    $version = ($parts[0..2] -join '.')
    if ($DryRun) {
        Write-Info "[dry-run] would bump version: $base -> $version (Directory.Build.props)"
    }
    else {
        Set-BaseVersion $version
        Write-Info "Bumped version: $base -> $version (written to Directory.Build.props)"
    }
}

if (-not $Tag) {
    $Tag = if ($NotPreRelease) { "v$version" } else { "v$version-$PreReleaseLabel" }
}
Write-Info "Tag: $Tag"

# The CI version job requires tag base == VersionPrefix; catch a mismatched explicit -Tag here.
if ($Tag -notmatch '^v(\d+\.\d+\.\d+)(-.+)?$' -or $Matches[1] -ne $version) {
    throw "Tag '$Tag' does not match version $version (CI will reject it)."
}

# ---- Commit the version bump -----------------------------------------------------------------
if (-not $NoBump) {
    Write-Step "Committing version bump ($version)"
    if ($DryRun) {
        Write-Info "[dry-run] would: git add Directory.Build.props && git commit -m 'chore: release $Tag'"
    }
    else {
        & git -C $repoRoot add $propsFile
        & git -C $repoRoot commit -m "chore: release $Tag"
        if ($LASTEXITCODE -ne 0) { throw "git commit failed." }
    }
}

# ---- Tag -------------------------------------------------------------------------------------
Write-Step "Tagging $Tag"
$existingTag = & git -C $repoRoot tag --list $Tag
if ($existingTag) { throw "Tag '$Tag' already exists. Choose another tag or delete it first." }
if ($DryRun) {
    Write-Info "[dry-run] would: git tag -a $Tag -m 'Sprocket $Tag'"
}
else {
    & git -C $repoRoot tag -a $Tag -m "Sprocket $Tag"
    if ($LASTEXITCODE -ne 0) { throw "git tag failed." }
}

# ---- Push branch + tag (the tag push triggers the release workflow) ---------------------------
Write-Step "Pushing '$branch' and tag '$Tag' to origin"
if ($DryRun) {
    Write-Info "[dry-run] would: git push origin $branch && git push origin $Tag"
    Write-Info "[dry-run] the tag push would trigger .github/workflows/release.yml"
    return
}
& git -C $repoRoot push origin $branch
if ($LASTEXITCODE -ne 0) { throw "git push (branch) failed." }
& git -C $repoRoot push origin $Tag
if ($LASTEXITCODE -ne 0) { throw "git push (tag) failed." }

# ---- Watch the CI release run ----------------------------------------------------------------
Write-Step 'Watching the CI release workflow'
Write-Info 'The Release workflow builds every OS, smoke-tests the artifacts, and publishes the release.'

# The run appears a few seconds after the push; poll briefly for it.
$runId = $null
foreach ($attempt in 1..15) {
    Start-Sleep -Seconds 4
    $runId = (& gh run list --workflow release.yml --branch $Tag --limit 1 --json databaseId --jq '.[0].databaseId' 2>$null)
    if ($runId) { break }
}
if ($runId) {
    & gh run watch $runId --exit-status
    if ($LASTEXITCODE -ne 0) { throw "The release workflow failed — see 'gh run view $runId'. No release was published." }
    Write-Host ""
    Write-Host "Done. Released $Tag." -ForegroundColor Cyan
    & gh release view $Tag --web 2>$null
}
else {
    Write-Info "Could not find the workflow run yet — watch it manually: gh run list --workflow release.yml"
}
