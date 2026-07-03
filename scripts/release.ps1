#!/usr/bin/env pwsh
#
# Sprocket release builder — the single build/package engine, run locally and by the CI release
# workflow (.github/workflows/release.yml).
#
# Publishes the Sprocket.App editor as a self-contained folder per target runtime (Windows / Linux /
# macOS — a FOLDER, not single-file: Velopack diffs the folder for delta updates and the macOS .app
# needs real structure), bundles the matching FFmpeg 8 + OpenAL Soft natives next to the exe, then
# produces the requested artifact kinds (-Package): a portable .zip and/or Velopack packages —
# Windows Setup.exe, Linux AppImage, macOS .app — with full/delta update feeds (PLAN.md steps 35–36;
# ARCHITECTURE.md §11, §14).
#
#   pwsh scripts/release.ps1                            # bump patch, build + zip every RID into ./dist
#   pwsh scripts/release.ps1 -Rids win-x64              # one RID only
#   pwsh scripts/release.ps1 -Package velopack,zip      # also produce Velopack packages (needs `vpk`)
#   pwsh scripts/release.ps1 -NoBump                    # release the current version without bumping
#   pwsh scripts/release.ps1 -Version 0.3.0             # publish an exact version (no bump / rewrite)
#   pwsh scripts/release.ps1 -NoZip                     # leave the publish folders, skip archiving
#   pwsh scripts/release.ps1 -NoFFmpeg                  # publish only, skip FFmpeg native bundling
#   pwsh scripts/release.ps1 -NoReadyToRun              # skip ReadyToRun AOT precompile (faster/smaller build)
#   pwsh scripts/release.ps1 -FFmpegCacheDir <dir>      # override the FFmpeg download cache (default ./.ffmpeg-cache)
#
# Velopack packaging (-Package velopack) needs the `vpk` CLI on PATH, at the SAME version as the
# Velopack NuGet in Sprocket.App.csproj ($VpkVersion below): dotnet tool install -g vpk --version <v>.
# A RID can only be packed on its own OS (the vpk CLI's option set is host-OS specific, and the macOS
# .app additionally needs codesign) — the CI release workflow provides the per-OS runners.
#
# Versioning: the X.Y.Z version lives in Directory.Build.props (<VersionPrefix>) as the single source
# of truth. Each release bumps the patch (third) number there and writes it back; the version is
# stamped into the published assemblies, the executable, and the artifact names. Bump major/minor by
# hand in Directory.Build.props.
#
# FFmpeg natives (ARCHITECTURE.md §11) — Sprocket's hand-rolled binding needs FFmpeg 8 shared libs at
# runtime (avcodec-62 / avutil-60 / avformat-62 / swscale-9 / swresample-6), found by the OS loader /
# FFmpegLoader in the app directory. There is NO FFmpeg-8 runtime NuGet for any RID, so EVERY platform's
# natives are fetched and bundled here:
#   * win-x64 / win-arm64      — downloaded from BtbN FFmpeg-Builds (n8 *-gpl-shared), .dll set copied
#                                next to the executable.
#   * linux-x64 / linux-arm64  — downloaded from BtbN FFmpeg-Builds (same source as
#                                scripts/linux-check.sh) and copied next to the executable.
#   * osx-x64 / osx-arm64      — BtbN publishes no macOS builds. When running ON macOS, Homebrew's
#                                FFmpeg 8 keg is bundled via scripts/macos-bundle-ffmpeg.sh (full
#                                transitive dylib closure, install names rewritten to @loader_path,
#                                re-signed ad-hoc). A caller-supplied -OsxX64FFmpegUrl /
#                                -OsxArm64FFmpegUrl archive overrides that; otherwise (not macOS, no
#                                URL) the bundle ships without FFmpeg and warns.
#
# OpenAL Soft natives (PLAN.md step 35) — bundled on every RID via the Silk.NET.OpenAL.Soft.Native
# NuGet, which lands in the publish folder automatically now that publishing is folder-based. One
# fix-up is required: the NuGet ships Linux's lib as `libopenal.so`, but Silk.NET probes
# `libopenal.so.1` — the copy is renamed so the app never falls back to a system OpenAL.
#
# FFmpeg download cache — the fetched archives are cached in -FFmpegCacheDir (default ./.ffmpeg-cache,
# git-ignored, kept OUTSIDE the wiped dist/) and persist across runs, so a release re-downloads FFmpeg only
# when upstream actually changed. A cached archive is reused only while its recorded version signature still
# matches the remote's — the GitHub asset id/updated_at/size for Windows+Linux (already in the releases API
# response, no extra request), or a HEAD ETag/Last-Modified/Content-Length for the caller-supplied macOS
# URLs. A signature mismatch (new upstream build) re-downloads automatically.

[CmdletBinding()]
param(
    # Runtime identifiers to publish. Defaults to the full cross-platform matrix.
    [string[]] $Rids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),

    # Exact version to publish (e.g. 0.3.0). When given, it is used verbatim and the source version
    # is NOT bumped or rewritten. When omitted, the patch number in Directory.Build.props is bumped.
    [string] $Version,

    # Optional prerelease suffix (e.g. alpha.1, beta.2, rc.1). When given it is appended to the
    # published version as "<version>-<suffix>" so it flows into the assembly's InformationalVersion
    # (and thus the in-app About box) and the artifact names. It is NOT written to
    # Directory.Build.props — only stamped into the published build.
    [string] $VersionSuffix,

    # Build configuration.
    [string] $Configuration = 'Release',

    # Output directory for the published bundles and zips, relative to the repo root.
    [string] $OutDir = 'dist',

    # Publish at the current Directory.Build.props version without bumping the patch (e.g. to
    # re-cut an artifact, or to release the version exactly as set). Ignored if -Version is given.
    [switch] $NoBump,

    # Skip zipping; leave the raw publish folders in place.
    [switch] $NoZip,

    # Do not wipe the output directory first. Lets CI accumulate artifacts across invocations with
    # different RID/package combinations (e.g. linux-x64 velopack+zip, then linux-arm64 zip-only).
    [switch] $NoClean,

    # Artifact kinds to produce per RID: 'zip' (portable folder zip) and/or 'velopack' (installer +
    # auto-update packages via the `vpk` CLI: Windows Setup.exe, Linux AppImage, macOS .app).
    [ValidateSet('zip', 'velopack')]
    [string[]] $Package = @('zip'),

    # Skip bundling FFmpeg native libraries entirely.
    [switch] $NoFFmpeg,

    # Skip ReadyToRun (R2R) AOT precompilation. R2R speeds cold start (~35% faster time-to-window) at
    # the cost of a larger artifact and a slower publish; skip it for faster local iteration builds.
    [switch] $NoReadyToRun,

    # Optional archive URLs (.tar.xz / .zip) of FFmpeg 8 macOS .dylib files to bundle.
    [string] $OsxX64FFmpegUrl,
    [string] $OsxArm64FFmpegUrl,

    # Where to cache downloaded FFmpeg archives between runs. Defaults to ./.ffmpeg-cache at the repo
    # root (git-ignored) — deliberately OUTSIDE the dist output, which is wiped on every run. Override
    # to share one cache across checkouts (e.g. a CI cache mount).
    [string] $FFmpegCacheDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src/Sprocket.App/Sprocket.App.csproj'
$distRoot = Join-Path $repoRoot $OutDir

# ── Velopack constants (PLAN.md step 36) ─────────────────────────────────────────────────────────
# The `vpk` CLI version must match the Velopack NuGet in Sprocket.App.csproj — pack/runtime version
# skew is the documented Velopack failure mode. Bump both together.
$VpkVersion = '1.2.0'
# packId + bundleId are PERMANENT once testers install (they key the install dir / update identity);
# the repo URL is baked into installed apps as the update feed (UpdateService.RepoUrl).
$VpkPackId = 'Sprocket'
$VpkBundleId = 'org.sprocketvideo.sprocket'
$VpkRepoUrl = 'https://github.com/SprocketVideo/Sprocket'
# The FFmpeg download cache must live OUTSIDE $distRoot, which is wiped on every run; keeping it here is
# what makes it persist between releases.
$ffCache = if ($FFmpegCacheDir) { $FFmpegCacheDir } else { Join-Path $repoRoot '.ffmpeg-cache' }
$propsFile = Join-Path $repoRoot 'Directory.Build.props'

# Read the X.Y.Z version from Directory.Build.props (<VersionPrefix>).
function Get-BaseVersion {
    $content = Get-Content $propsFile -Raw
    if ($content -match '<VersionPrefix>\s*([^<\s]+)\s*</VersionPrefix>') { return $Matches[1] }
    throw "No <VersionPrefix> found in $propsFile"
}

# Write a new X.Y.Z version back into <VersionPrefix>, preserving the rest of the file verbatim.
function Set-BaseVersion([string] $v) {
    $content = Get-Content $propsFile -Raw
    $updated = [regex]::Replace($content, '(<VersionPrefix>)\s*[^<\s]+\s*(</VersionPrefix>)', "`${1}$v`${2}")
    Set-Content -Path $propsFile -Value $updated -NoNewline
}

# Resolve the version to publish: an explicit -Version wins (no rewrite); otherwise bump the patch
# (third) number in Directory.Build.props and write it back, unless -NoBump was given.
if (-not $Version) {
    $base = Get-BaseVersion
    if ($NoBump) {
        $Version = $base
    }
    else {
        $parts = $base.Split('.')
        if ($parts.Count -lt 3) { throw "VersionPrefix '$base' is not in X.Y.Z form." }
        $parts[2] = [string]([int]$parts[2] + 1)
        $Version = ($parts[0..2] -join '.')
        Set-BaseVersion $Version
        Write-Host "Bumped version: $base -> $Version (written to Directory.Build.props)" -ForegroundColor Cyan
    }
}

# The full display/stamp version: the X.Y.Z source version plus any prerelease suffix. This is what
# gets stamped into the assemblies (InformationalVersion -> About box) and the artifact names; the
# numeric AssemblyVersion/FileVersion still derive from the X.Y.Z prefix.
$fullVersion = if ($VersionSuffix) { "$Version-$VersionSuffix" } else { $Version }

# RID -> BtbN FFmpeg-Builds platform token for the *-gpl-shared archives. Every Windows + Linux RID is
# sourced here now (FFmpeg 8 has no runtime NuGet); macOS is caller-supplied via the -Osx*FFmpegUrl params.
$btbnPlatform = @{
    'win-x64'     = 'win64'
    'win-arm64'   = 'winarm64'
    'linux-x64'   = 'linux64'
    'linux-arm64' = 'linuxarm64'
}

# Resolve (once) the BtbN release asset for a platform token from the rolling "latest" release,
# matching the FFmpeg 8 gpl-shared asset — the same selection scripts/linux-check.sh makes. Returns the
# full asset object (its id/updated_at/size feed the cache version signature; .browser_download_url is
# the download URL).
$script:btbnAssets = $null
function Get-BtbnAsset([string] $platform) {
    if ($null -eq $script:btbnAssets) {
        $headers = @{ 'User-Agent' = 'sprocket-release' }
        if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }
        $rel = Invoke-RestMethod -Headers $headers `
            -Uri 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest'
        $script:btbnAssets = $rel.assets
    }
    $asset = $script:btbnAssets |
        Where-Object { $_.name -match "$platform-gpl-shared" -and $_.name -match 'n8\.' -and $_.name -notmatch '\.sha256$' } |
        Select-Object -First 1
    if (-not $asset) { throw "No BtbN FFmpeg 8 gpl-shared asset found for platform '$platform'." }
    return $asset
}

# Compute a stable "version signature" for a remote archive, so a cached copy can be reused only while
# upstream is unchanged. For a BtbN GitHub asset the releases API already handed us rich metadata (no
# extra request); for a caller-supplied macOS URL we probe with a single HEAD request. Returns $null when
# the remote version can't be determined — in that case a present cache is trusted as-is.
function Get-RemoteSignature([string] $url, $apiAsset) {
    if ($apiAsset) {
        return "gh:id=$($apiAsset.id);updated=$($apiAsset.updated_at);size=$($apiAsset.size)"
    }
    try {
        $resp = Invoke-WebRequest -Method Head -Uri $url -MaximumRedirection 5 `
            -Headers @{ 'User-Agent' = 'sprocket-release' }
        $etag = ($resp.Headers['ETag'] -join ',')
        $lm = ($resp.Headers['Last-Modified'] -join ',')
        $len = ($resp.Headers['Content-Length'] -join ',')
        if (-not ($etag -or $lm -or $len)) { return $null }
        return "head:etag=$etag;lm=$lm;len=$len"
    }
    catch {
        Write-Host "    [warn] HEAD probe failed for $url — trusting the cached copy if present." -ForegroundColor Yellow
        return $null
    }
}

# Download (persistently cached) and extract an archive, returning the extraction directory. The cache
# lives in $ffCache (outside the wiped dist/) and is keyed by the version signature above: a cached
# archive is reused only when its stored .sig still matches the remote's, so a new upstream build is
# picked up automatically. The extracted tree is likewise reused when the archive was a cache hit.
function Expand-RemoteArchive([string] $url, [string] $tag, $apiAsset) {
    New-Item -ItemType Directory -Path $ffCache -Force | Out-Null
    $fileName = Split-Path -Leaf ($url -split '\?')[0]
    $archive = Join-Path $ffCache $fileName
    $sigFile = "$archive.sig"

    $signature = Get-RemoteSignature $url $apiAsset
    $cacheHit = $false
    if (Test-Path $archive) {
        if ($null -eq $signature) {
            $cacheHit = $true  # remote version unknown — trust the cached file
        }
        elseif ((Test-Path $sigFile) -and ((Get-Content $sigFile -Raw).Trim() -eq $signature)) {
            $cacheHit = $true
        }
    }

    if ($cacheHit) {
        Write-Host "    using cached FFmpeg ($tag): $fileName"
    }
    else {
        Write-Host "    downloading FFmpeg ($tag): $fileName"
        Invoke-WebRequest -Uri $url -OutFile $archive -Headers @{ 'User-Agent' = 'sprocket-release' }
        if ($signature) { Set-Content -Path $sigFile -Value $signature -NoNewline }
        elseif (Test-Path $sigFile) { Remove-Item $sigFile -Force }
    }

    $dest = Join-Path $ffCache "extract-$tag"
    # Reuse the extracted tree only on a cache hit AND when it actually holds files (guards against a
    # half-finished extraction from an interrupted run).
    $extractValid = $cacheHit -and (Test-Path $dest) -and
        ($null -ne (Get-ChildItem $dest -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1))
    if ($extractValid) {
        Write-Host "    using cached FFmpeg extraction ($tag)"
        return $dest
    }

    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    if ($fileName -match '\.zip$') {
        Expand-Archive -Path $archive -DestinationPath $dest -Force
    }
    else {
        # .tar.xz — Windows 11 / Linux / macOS all ship bsdtar, which handles xz.
        tar -xf $archive -C $dest
        if ($LASTEXITCODE -ne 0) { throw "tar failed to extract $fileName" }
    }
    return $dest
}

# On macOS, rewrite each bundled dylib's install name (id) and its references to sibling FFmpeg dylibs
# to @loader_path so the set loads from the app folder with no Homebrew/absolute paths — the structural
# fix that lets a macOS bundle run with no user setup (ARCHITECTURE.md §11). Needs Apple's
# install_name_tool + otool, so it only runs when they are present (i.e. building on macOS); elsewhere
# it warns and leaves the dylibs as-is (they would still need DYLD_FALLBACK_LIBRARY_PATH to load).
function Repair-MacosInstallNames([string] $publishDir, [string[]] $libNames) {
    if (-not (Get-Command install_name_tool -ErrorAction SilentlyContinue) -or
        -not (Get-Command otool -ErrorAction SilentlyContinue)) {
        Write-Host "    [warn] install_name_tool/otool not found — macOS dylib install names NOT rewritten" -ForegroundColor Yellow
        Write-Host "           to @loader_path. Run the macOS bundling step on a Mac for a self-contained app." -ForegroundColor Yellow
        return
    }
    $set = [System.Collections.Generic.HashSet[string]]::new([string[]]$libNames)
    foreach ($name in $libNames) {
        $path = Join-Path $publishDir $name
        & install_name_tool -id "@loader_path/$name" $path 2>$null
        # Rewrite every dependency that points at one of our sibling dylibs to @loader_path.
        foreach ($line in (& otool -L $path)) {
            $dep = ($line.Trim() -split '\s+')[0]
            if (-not $dep) { continue }
            $depName = Split-Path -Leaf $dep
            if ($set.Contains($depName) -and $dep -ne "@loader_path/$depName") {
                & install_name_tool -change $dep "@loader_path/$depName" $path 2>$null
            }
        }
    }
    Write-Host "    rewrote $($libNames.Count) macOS dylib install names to @loader_path"
}

# Bundle FFmpeg natives into a freshly published RID folder. Returns $true if libs were placed.
function Add-FFmpegNatives([string] $rid, [string] $publishDir) {
    # Resolve the download source: BtbN for every Windows/Linux RID, caller-supplied URL or (on a
    # macOS host) Homebrew for macOS. The BtbN path also yields the release asset, whose metadata
    # drives the cache version signature.
    $url = $null
    $asset = $null
    if ($btbnPlatform.ContainsKey($rid)) {
        $asset = Get-BtbnAsset $btbnPlatform[$rid]
        $url = $asset.browser_download_url
    }
    elseif ($rid -eq 'osx-x64' -and $OsxX64FFmpegUrl) { $url = $OsxX64FFmpegUrl }
    elseif ($rid -eq 'osx-arm64' -and $OsxArm64FFmpegUrl) { $url = $OsxArm64FFmpegUrl }
    elseif ($rid -like 'osx-*' -and $IsMacOS) {
        # BtbN has no macOS builds: bundle Homebrew's FFmpeg 8 keg — the full transitive dylib
        # closure, install names rewritten to @loader_path, re-signed (scripts/macos-bundle-ffmpeg.sh
        # asserts libavcodec major 62 and fails loudly on formula drift). Only sane when the host
        # arch matches the RID (brew installs native-arch bottles), which the CI matrix guarantees.
        & bash (Join-Path $PSScriptRoot 'macos-bundle-ffmpeg.sh') $publishDir
        if ($LASTEXITCODE -ne 0) { throw "macos-bundle-ffmpeg.sh failed for $rid (exit $LASTEXITCODE)" }
        return $true
    }

    if (-not $url) {
        return $false  # no source available (macOS off-Mac without a URL)
    }

    $extract = Expand-RemoteArchive $url $rid $asset
    # Shared libs live in lib/ (Linux .so*, macOS .dylib) or bin/ (Windows .dll); recurse to be safe.
    $libs = Get-ChildItem -Path $extract -Recurse -File |
        Where-Object { $_.Name -match '\.(so[\.\d]*|dylib|dll)$' -and $_.Name -notmatch '^lib(x264|x265)' } |
        Where-Object { $_.Name -match '^(lib)?(av|sw|postproc)' }
    if (-not $libs) { throw "No FFmpeg shared libraries found in the $rid archive." }
    foreach ($lib in $libs) {
        Copy-Item $lib.FullName -Destination (Join-Path $publishDir $lib.Name) -Force
    }
    Write-Host "    bundled $($libs.Count) FFmpeg libs into $rid"

    if ($rid -like 'osx-*') {
        Repair-MacosInstallNames $publishDir ($libs | ForEach-Object { $_.Name })
    }
    return $true
}

# Bundle Linux desktop integration into a published linux-* folder: the app icon plus a .desktop template and
# install/uninstall scripts (packaging/linux/) that register an application launcher + hicolor icon for the
# current user, so Sprocket gets a proper icon in the applications menu / dock / file browser (PLAN.md step 36
# — desktop integration; the AppImage/tarball + code-signing parts of that step are still outstanding).
function Add-LinuxDesktopIntegration([string] $publishDir) {
    $srcDir = Join-Path $repoRoot 'packaging/linux'
    $icon = Join-Path $repoRoot 'src/Sprocket.App/Assets/sprocket.png'
    foreach ($f in @('install.sh', 'uninstall.sh', 'sprocket.desktop')) {
        Copy-Item (Join-Path $srcDir $f) -Destination (Join-Path $publishDir $f) -Force
    }
    Copy-Item $icon -Destination (Join-Path $publishDir 'sprocket.png') -Force
    Write-Host "    bundled Linux desktop integration (install.sh + icon)"
}

# OpenAL Soft fix-up (PLAN.md step 35): the Silk.NET.OpenAL.Soft.Native NuGet lands its per-RID
# native in the folder publish automatically, but ships Linux's lib as `libopenal.so` while
# Silk.NET's loader probes `libopenal.so.1` — without the rename Linux silently falls back to a
# system OpenAL (or the software clock). Also asserts the native actually exists per RID, so a NuGet
# layout change fails the release loudly instead of shipping a silent audio regression.
function Add-OpenAlNatives([string] $rid, [string] $publishDir) {
    $expected = switch -Wildcard ($rid) {
        'win-*'   { 'soft_oal.dll' }
        'linux-*' { 'libopenal.so' }
        'osx-*'   { 'libopenal.dylib' }
    }
    $path = Join-Path $publishDir $expected
    if (-not (Test-Path $path)) {
        throw "OpenAL native '$expected' missing from the $rid publish output — did the Silk.NET.OpenAL.Soft.Native layout change?"
    }
    if ($rid -like 'linux-*') {
        Copy-Item $path -Destination (Join-Path $publishDir 'libopenal.so.1') -Force
        Write-Host "    OpenAL: libopenal.so -> libopenal.so.1 (Silk.NET probe name)"
    }
    else {
        Write-Host "    OpenAL: $expected present"
    }
}

# Generate the macOS .icns app icon from the repo's 1024x1024 PNG using Apple's sips + iconutil
# (macOS-only tools) — no binary .icns is committed to the repo. Returns the generated file's path.
function New-MacIcns {
    $png = Join-Path $repoRoot 'src/Sprocket.App/Assets/sprocket.png'
    $iconset = Join-Path $distRoot 'sprocket.iconset'
    $icns = Join-Path $distRoot 'sprocket.icns'
    if (Test-Path $icns) { return $icns }
    if (Test-Path $iconset) { Remove-Item $iconset -Recurse -Force }
    New-Item -ItemType Directory -Path $iconset -Force | Out-Null
    foreach ($size in 16, 32, 64, 128, 256, 512) {
        & sips -z $size $size $png --out (Join-Path $iconset "icon_${size}x${size}.png") | Out-Null
        $2x = $size * 2
        & sips -z $2x $2x $png --out (Join-Path $iconset "icon_${size}x${size}@2x.png") | Out-Null
    }
    & iconutil -c icns $iconset -o $icns
    if ($LASTEXITCODE -ne 0) { throw "iconutil failed to build sprocket.icns" }
    Remove-Item $iconset -Recurse -Force
    Write-Host "    generated sprocket.icns from Assets/sprocket.png"
    return $icns
}

# Pack a published RID folder with Velopack (PLAN.md step 36): Windows -> Setup.exe (+ portable zip),
# Linux -> self-updating AppImage, macOS -> .app (in a portable zip, + .pkg). Every RID is its own
# update channel (multi-arch requires it; UpdateManager follows the channel baked in at pack time).
# Previously published packages for the channel are fetched first (best-effort) so `vpk` can emit a
# delta package alongside the full one.
function Add-VelopackPackage([string] $rid, [string] $publishDir) {
    if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
        throw "Velopack packaging requested but the 'vpk' CLI is not on PATH. Install it with: dotnet tool install -g vpk --version $VpkVersion"
    }

    # The vpk CLI's option set is host-OS specific (--categories exists only on Linux, --bundleId
    # only on macOS, the Setup/MSI options only on Windows), so packing a RID requires a matching
    # host OS — which the CI matrix provides (win-arm64 from the win-x64 runner is same-OS and fine).
    $hostOsMatches = switch -Wildcard ($rid) {
        'win-*'   { $IsWindows }
        'linux-*' { $IsLinux }
        'osx-*'   { $IsMacOS }
    }
    if (-not $hostOsMatches) {
        throw "Velopack packages for $rid can only be packed on that OS (the vpk CLI is host-OS specific) — use the CI release workflow, or -Package zip."
    }

    $vpkOut = Join-Path $distRoot "velopack/$rid"
    New-Item -ItemType Directory -Path $vpkOut -Force | Out-Null

    # Fetch the channel's previously published packages so this pack can produce a delta. Best-effort:
    # the very first release (or an offline run) has nothing to diff against — full packages still work.
    # A token avoids anonymous GitHub API rate limits on shared CI runner IPs.
    $dlArgs = @('download', 'github', '--repoUrl', $VpkRepoUrl, '--channel', $rid, '--outputDir', $vpkOut, '--pre')
    if ($env:GITHUB_TOKEN) { $dlArgs += @('--token', $env:GITHUB_TOKEN) }
    & vpk @dlArgs 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "    [warn] vpk download github failed — no delta baseline; packing full only." -ForegroundColor Yellow
    }

    $mainExe = if ($rid -like 'win-*') { 'Sprocket.exe' } else { 'Sprocket' }
    $vpkArgs = @(
        'pack',
        '--runtime', $rid,
        '--packId', $VpkPackId,
        '--packVersion', $fullVersion,
        '--packDir', $publishDir,
        '--mainExe', $mainExe,
        '--packTitle', 'Sprocket',
        '--packAuthors', 'SprocketVideo',
        '--channel', $rid,
        '--outputDir', $vpkOut
    )
    if ($rid -like 'win-*') {
        $vpkArgs += @('--icon', (Join-Path $repoRoot 'src/Sprocket.App/Assets/sprocket.ico'))
    }
    elseif ($rid -like 'linux-*') {
        # Mirrors packaging/linux/sprocket.desktop; the AppImage embeds its own .desktop + icon.
        $vpkArgs += @(
            '--icon', (Join-Path $repoRoot 'src/Sprocket.App/Assets/sprocket.png'),
            '--categories', 'AudioVideo;AudioVideoEditing;Video;'
        )
    }
    else {
        $vpkArgs += @('--icon', (New-MacIcns), '--bundleId', $VpkBundleId)
    }

    & vpk @vpkArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed for $rid (exit $LASTEXITCODE)" }
    Write-Host "    velopack package -> $vpkOut"
}

if (-not (Test-Path $appProject)) {
    throw "Cannot find app project at $appProject"
}

Write-Host "Sprocket release build" -ForegroundColor Cyan
Write-Host "  version:       $fullVersion"
Write-Host "  configuration: $Configuration"
Write-Host "  runtimes:      $($Rids -join ', ')"
Write-Host "  packages:      $($Package -join ', ')$(if ($NoZip) { ' (zip suppressed by -NoZip)' })"
Write-Host "  ffmpeg:        $(if ($NoFFmpeg) { 'skipped (-NoFFmpeg)' } else { 'bundled' })"
Write-Host "  readytorun:    $(if ($NoReadyToRun) { 'skipped (-NoReadyToRun)' } else { 'enabled' })"
Write-Host "  output:        $distRoot"
Write-Host ""

# Start from a clean dist so stale artifacts never ship (unless CI is accumulating runs, -NoClean).
if ((Test-Path $distRoot) -and -not $NoClean) {
    Remove-Item $distRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null

$results = @()

foreach ($rid in $Rids) {
    Write-Host "==> publishing $rid" -ForegroundColor Green
    $publishDir = Join-Path $distRoot "Sprocket-$fullVersion-$rid"

    # Folder publish, NOT single-file: Velopack's delta updates diff the publish folder file-by-file,
    # and the macOS .app needs its real structure. This is also what lands the per-RID NuGet natives
    # (SkiaSharp, OpenAL Soft) loose next to the exe where the loaders expect them.
    $publishArgs = @(
        'publish', $appProject,
        '-c', $Configuration,
        '-r', $rid,
        '--self-contained', 'true',
        '-o', $publishDir,
        "-p:Version=$fullVersion",
        # Managed symbols are embedded into the assemblies — see Directory.Build.props. No loose
        # .pdb files ship.
        '-p:DebugType=embedded',
        '--nologo'
    )

    # ReadyToRun: AOT-precompile the app AND its managed deps (Avalonia / SkiaSharp) so cold start skips
    # JIT. Measured ~35% faster time-to-window on win-x64 (~1.5s JIT -> ~1.0s) at the cost of a larger
    # artifact and a slower publish; the residual is framework init + UI-tree construction. crossgen2
    # cross-compiles for every RID in the matrix from one host, but R2R is per-RID native code — smoke
    # test each published artifact (it launches) on its target OS. -NoReadyToRun skips it entirely for a
    # faster, smaller build (e.g. quick local iteration) at the cost of slower cold start.
    if (-not $NoReadyToRun) {
        $publishArgs += '-p:PublishReadyToRun=true'
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid (exit $LASTEXITCODE)"
    }

    $hasFFmpeg = $true
    if (-not $NoFFmpeg) {
        $hasFFmpeg = Add-FFmpegNatives $rid $publishDir
    }
    if (-not $hasFFmpeg) {
        Write-Host "    [warn] $rid ships without FFmpeg natives — decode will not work until the" -ForegroundColor Yellow
        Write-Host "           FFmpeg 8 .dylib files are placed next to the executable. Re-run with" -ForegroundColor Yellow
        Write-Host "           -OsxX64FFmpegUrl / -OsxArm64FFmpegUrl to bundle them (ARCHITECTURE.md §11)." -ForegroundColor Yellow
    }

    Add-OpenAlNatives $rid $publishDir

    if ($rid -like 'linux-*') {
        Add-LinuxDesktopIntegration $publishDir
    }

    if ($Package -contains 'velopack') {
        Add-VelopackPackage $rid $publishDir
    }

    if ($NoZip -or $Package -notcontains 'zip') {
        # Keep the publish folder — it is the artifact (and what CI smoke-launches directly).
        $results += [pscustomobject]@{ Rid = $rid; Artifact = $publishDir; FFmpeg = $hasFFmpeg }
        continue
    }

    $zipPath = "$publishDir.zip"
    Write-Host "    archiving -> $(Split-Path -Leaf $zipPath)"
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -Force
    Remove-Item $publishDir -Recurse -Force
    $results += [pscustomobject]@{ Rid = $rid; Artifact = $zipPath; FFmpeg = $hasFFmpeg }
}

# The download cache ($ffCache) is deliberately kept between runs and never lands in the shipped output:
# it lives outside $distRoot and only the extracted .dll/.so/.dylib files are copied into each bundle.

Write-Host ""
Write-Host "Done. Artifacts in $distRoot" -ForegroundColor Cyan
Write-Host "  ffmpeg cache:  $ffCache (persisted; re-downloaded only when upstream changes)"
if ($Package -contains 'velopack') {
    Write-Host "  velopack:      $(Join-Path $distRoot 'velopack')/<rid> (installer + full/delta packages + releases.<rid>.json)"
}
$results | ForEach-Object {
    $size = if (Test-Path $_.Artifact) { '{0:N1} MB' -f ((Get-Item $_.Artifact).Length / 1MB) } else { 'dir' }
    $ff = if ($_.FFmpeg) { 'ffmpeg ok' } else { 'NO ffmpeg' }
    Write-Host ("  {0,-12} {1,-40} ({2}, {3})" -f $_.Rid, (Split-Path -Leaf $_.Artifact), $size, $ff)
}
