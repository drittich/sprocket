#!/usr/bin/env pwsh
#
# Sprocket release builder.
#
# Publishes the Sprocket.App editor as a self-contained, single-file executable for each target
# runtime (Windows / Linux / macOS), bundles the matching FFmpeg 7 native libraries next to it, then
# zips each bundle into dist/. One managed build serves all three OSes; only the bundled native libs
# differ per RID (ARCHITECTURE.md §11, §14).
#
#   pwsh scripts/release.ps1                       # build + bundle every RID into ./dist
#   pwsh scripts/release.ps1 -Rids win-x64         # one RID only
#   pwsh scripts/release.ps1 -Version 0.3.0        # stamp a version into the artifact names
#   pwsh scripts/release.ps1 -NoZip                # leave the publish folders, skip archiving
#   pwsh scripts/release.ps1 -NoFFmpeg             # publish only, skip FFmpeg native bundling
#
# FFmpeg natives (ARCHITECTURE.md §11) — the bindings need FFmpeg 7.1 shared libs at runtime, found
# by the OS loader in the app directory (the app sets no RootPath). They are sourced per RID:
#   * win-x64                  — embedded from the Sdcb.FFmpeg.runtime.windows-x64 NuGet during
#                                publish (nothing to fetch).
#   * linux-x64 / linux-arm64  — downloaded from BtbN FFmpeg-Builds (same source as
#     / win-arm64                scripts/linux-check.sh) and copied next to the executable.
#   * osx-x64 / osx-arm64      — no canonical automated shared-dylib build exists. Pass a URL to an
#                                archive of FFmpeg 7.1 .dylib files via -OsxX64FFmpegUrl /
#                                -OsxArm64FFmpegUrl to have them bundled; otherwise the macOS bundle
#                                ships without FFmpeg and the script warns.

[CmdletBinding()]
param(
    # Runtime identifiers to publish. Defaults to the full cross-platform matrix.
    [string[]] $Rids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),

    # Version string stamped into the zip names (e.g. 0.3.0). Defaults to a date-based dev tag.
    [string] $Version = "0.0.0-dev",

    # Build configuration.
    [string] $Configuration = 'Release',

    # Output directory for the published bundles and zips, relative to the repo root.
    [string] $OutDir = 'dist',

    # Skip zipping; leave the raw publish folders in place.
    [switch] $NoZip,

    # Skip bundling FFmpeg native libraries entirely.
    [switch] $NoFFmpeg,

    # Optional archive URLs (.tar.xz / .zip) of FFmpeg 7.1 macOS .dylib files to bundle.
    [string] $OsxX64FFmpegUrl,
    [string] $OsxArm64FFmpegUrl
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src/Sprocket.App/Sprocket.App.csproj'
$distRoot = Join-Path $repoRoot $OutDir
$ffCache = Join-Path $distRoot '.ffmpeg-cache'

# RID -> BtbN FFmpeg-Builds platform token for the *-gpl-shared archives.
$btbnPlatform = @{
    'linux-x64'   = 'linux64'
    'linux-arm64' = 'linuxarm64'
    'win-arm64'   = 'winarm64'
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
        Where-Object { $_.name -match "$platform-gpl-shared" -and $_.name -match 'n7\.' -and $_.name -notmatch '\.sha256$' } |
        Select-Object -First 1
    if (-not $asset) { throw "No BtbN FFmpeg 7 gpl-shared asset found for platform '$platform'." }
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

# Bundle FFmpeg natives into a freshly published RID folder. Returns $true if libs were placed.
function Add-FFmpegNatives([string] $rid, [string] $publishDir) {
    if ($rid -eq 'win-x64') {
        return $true   # embedded from the Sdcb NuGet during publish
    }

    # Resolve the download URL: BtbN for Linux/Win-arm64, caller-supplied for macOS.
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
    return $true
}

if (-not (Test-Path $appProject)) {
    throw "Cannot find app project at $appProject"
}

Write-Host "Sprocket release build" -ForegroundColor Cyan
Write-Host "  version:       $Version"
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
    $publishDir = Join-Path $distRoot "Sprocket-$Version-$rid"

    $publishArgs = @(
        'publish', $appProject,
        '-c', $Configuration,
        '-r', $rid,
        '--self-contained', 'true',
        '-o', $publishDir,
        "-p:Version=$Version",
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=none',
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
        Write-Host "           FFmpeg 7.1 .dylib files are placed next to the executable. Re-run with" -ForegroundColor Yellow
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
