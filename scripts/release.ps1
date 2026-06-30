#!/usr/bin/env pwsh
#
# Sprocket release builder.
#
# Publishes the Sprocket.App editor as a self-contained, single-file executable for each target
# runtime (Windows / Linux / macOS), bundles the matching FFmpeg 8 native libraries next to it, then
# zips each bundle into dist/. One managed build serves all three OSes; only the bundled native libs
# differ per RID (ARCHITECTURE.md §11, §14).
#
#   pwsh scripts/release.ps1                       # bump patch, build + bundle every RID into ./dist
#   pwsh scripts/release.ps1 -Rids win-x64         # one RID only
#   pwsh scripts/release.ps1 -NoBump               # release the current version without bumping
#   pwsh scripts/release.ps1 -Version 0.3.0        # publish an exact version (no bump / rewrite)
#   pwsh scripts/release.ps1 -NoZip                # leave the publish folders, skip archiving
#   pwsh scripts/release.ps1 -NoFFmpeg             # publish only, skip FFmpeg native bundling
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
#                                next to the executable. (win-x64 is no longer embedded — the dormant
#                                Sdcb.FFmpeg runtime NuGet was dropped in the FFmpeg-8 migration.)
#   * linux-x64 / linux-arm64  — downloaded from BtbN FFmpeg-Builds (same source as
#                                scripts/linux-check.sh) and copied next to the executable.
#   * osx-x64 / osx-arm64      — no canonical automated shared-dylib build exists. Pass a URL to an
#                                archive of FFmpeg 8 .dylib files via -OsxX64FFmpegUrl /
#                                -OsxArm64FFmpegUrl to have them bundled; the script then rewrites their
#                                install names to @loader_path (when run on macOS) so the bundle is
#                                self-contained. Otherwise the macOS bundle ships without FFmpeg and warns.

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

    # Skip bundling FFmpeg native libraries entirely.
    [switch] $NoFFmpeg,

    # Optional archive URLs (.tar.xz / .zip) of FFmpeg 8 macOS .dylib files to bundle.
    [string] $OsxX64FFmpegUrl,
    [string] $OsxArm64FFmpegUrl
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src/Sprocket.App/Sprocket.App.csproj'
$distRoot = Join-Path $repoRoot $OutDir
$ffCache = Join-Path $distRoot '.ffmpeg-cache'
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

# Resolve (once) the BtbN download URL for a platform token from the rolling "latest" release,
# matching the FFmpeg 7 gpl-shared asset — the same selection scripts/linux-check.sh makes.
$script:btbnAssets = $null
function Get-BtbnUrl([string] $platform) {
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
    return $asset.browser_download_url
}

# Download (cached) and extract an archive, returning the extraction directory.
function Expand-RemoteArchive([string] $url, [string] $tag) {
    New-Item -ItemType Directory -Path $ffCache -Force | Out-Null
    $fileName = Split-Path -Leaf ($url -split '\?')[0]
    $archive = Join-Path $ffCache $fileName
    if (-not (Test-Path $archive)) {
        Write-Host "    downloading FFmpeg ($tag): $fileName"
        Invoke-WebRequest -Uri $url -OutFile $archive -Headers @{ 'User-Agent' = 'sprocket-release' }
    }
    $dest = Join-Path $ffCache "extract-$tag"
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
    # Resolve the download URL: BtbN for every Windows/Linux RID, caller-supplied for macOS.
    $url = $null
    if ($btbnPlatform.ContainsKey($rid)) {
        $url = Get-BtbnUrl $btbnPlatform[$rid]
    }
    elseif ($rid -eq 'osx-x64' -and $OsxX64FFmpegUrl) { $url = $OsxX64FFmpegUrl }
    elseif ($rid -eq 'osx-arm64' -and $OsxArm64FFmpegUrl) { $url = $OsxArm64FFmpegUrl }

    if (-not $url) {
        return $false  # no source available (macOS without a URL)
    }

    $extract = Expand-RemoteArchive $url $rid
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

if (-not (Test-Path $appProject)) {
    throw "Cannot find app project at $appProject"
}

Write-Host "Sprocket release build" -ForegroundColor Cyan
Write-Host "  version:       $fullVersion"
Write-Host "  configuration: $Configuration"
Write-Host "  runtimes:      $($Rids -join ', ')"
Write-Host "  ffmpeg:        $(if ($NoFFmpeg) { 'skipped (-NoFFmpeg)' } else { 'bundled' })"
Write-Host "  output:        $distRoot"
Write-Host ""

# Start from a clean dist so stale artifacts never ship.
if (Test-Path $distRoot) {
    Remove-Item $distRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null

$results = @()

foreach ($rid in $Rids) {
    Write-Host "==> publishing $rid" -ForegroundColor Green
    $publishDir = Join-Path $distRoot "Sprocket-$fullVersion-$rid"

    $publishArgs = @(
        'publish', $appProject,
        '-c', $Configuration,
        '-r', $rid,
        '--self-contained', 'true',
        '-o', $publishDir,
        "-p:Version=$fullVersion",
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        # Managed symbols are embedded into the assemblies (which are bundled into the single-file
        # exe) — see Directory.Build.props. No loose .pdb files ship.
        '-p:DebugType=embedded',
        '--nologo'
    )

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

    if ($NoZip) {
        $results += [pscustomobject]@{ Rid = $rid; Artifact = $publishDir; FFmpeg = $hasFFmpeg }
        continue
    }

    $zipPath = "$publishDir.zip"
    Write-Host "    archiving -> $(Split-Path -Leaf $zipPath)"
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -Force
    Remove-Item $publishDir -Recurse -Force
    $results += [pscustomobject]@{ Rid = $rid; Artifact = $zipPath; FFmpeg = $hasFFmpeg }
}

# Drop the download cache so it never lands in the shipped output.
if (Test-Path $ffCache) { Remove-Item $ffCache -Recurse -Force }

Write-Host ""
Write-Host "Done. Artifacts in $distRoot" -ForegroundColor Cyan
$results | ForEach-Object {
    $size = if (Test-Path $_.Artifact) { '{0:N1} MB' -f ((Get-Item $_.Artifact).Length / 1MB) } else { 'dir' }
    $ff = if ($_.FFmpeg) { 'ffmpeg ok' } else { 'NO ffmpeg' }
    Write-Host ("  {0,-12} {1,-40} ({2}, {3})" -f $_.Rid, (Split-Path -Leaf $_.Artifact), $size, $ff)
}
