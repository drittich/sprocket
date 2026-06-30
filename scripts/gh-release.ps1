#!/usr/bin/env pwsh
#
# Sprocket GitHub release publisher.
#
# Thin orchestration layer on top of scripts/release.ps1. It builds the per-RID, self-contained
# bundles (release.ps1), commits the version bump, creates + pushes an annotated git tag, then
# creates the GitHub release with `gh release create` and attaches every dist/*.zip as an asset.
#
#   pwsh scripts/gh-release.ps1                      # bump patch, build all 6 RIDs, tag v<ver>-alpha.1, prerelease
#   pwsh scripts/gh-release.ps1 -DryRun              # show every step without changing git or GitHub
#   pwsh scripts/gh-release.ps1 -NoBump              # release the current Directory.Build.props version as-is
#   pwsh scripts/gh-release.ps1 -Rids win-x64,linux-x64
#   pwsh scripts/gh-release.ps1 -Tag v0.2.0-alpha.1  # use an exact git tag / release name
#   pwsh scripts/gh-release.ps1 -PreReleaseLabel beta.2          # tag v<ver>-beta.2
#   pwsh scripts/gh-release.ps1 -OsxX64FFmpegUrl <url> -OsxArm64FFmpegUrl <url>   # bundle macOS FFmpeg
#
# Versioning: release.ps1 owns the X.Y.Z version (Directory.Build.props <VersionPrefix>) and stamps
# it into the assemblies. The GIT TAG carries the prerelease suffix (e.g. v0.1.20-alpha.1); the
# binaries themselves report the plain numeric version. The bump (if any) is committed before the
# tag so the tag points at a commit that contains its own version.
#
# macOS / FFmpeg — pass -OsxX64FFmpegUrl / -OsxArm64FFmpegUrl with archives of FFmpeg 8 .dylibs.
# release.ps1 copies the libav*/libsw*/libpostproc dylibs next to the exe AND, when run ON macOS
# (install_name_tool/otool present), rewrites each dylib's id + its sibling references to
# @loader_path — so the bundle loads from the app folder with no Homebrew/absolute paths and the user
# does nothing. IMPORTANT: that rewrite only happens on a Mac build host; cross-building macOS bundles
# from Windows/Linux copies the dylibs but cannot rewrite their Mach-O install names, so cut the macOS
# RIDs on a macOS runner for a genuinely self-contained app. The remaining macOS work is code-signing
# + notarization + a proper .app (PLAN.md step 36), not a flag here. Do NOT ship a second "no-FFmpeg"
# macOS variant: a macOS build without FFmpeg can't decode/play/export at all — a broken download.
#
# Requires: gh (authenticated — run `gh auth login`), a clean working tree, and the same toolchain
# release.ps1 needs (dotnet 10, plus network access for the FFmpeg native downloads).

[CmdletBinding()]
param(
    # Runtime identifiers to publish. Defaults to the full cross-platform matrix (matches release.ps1).
    [string[]] $Rids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),

    # Exact git tag / GitHub release name. When omitted it is derived from the built version as
    # "v<version>-<PreReleaseLabel>".
    [string] $Tag,

    # Prerelease suffix used when -Tag is not given (e.g. alpha.1, beta.2, rc.1).
    [string] $PreReleaseLabel = 'alpha.1',

    # Release the current Directory.Build.props version without bumping the patch.
    [switch] $NoBump,

    # Mark the GitHub release as a full release instead of a prerelease.
    [switch] $NotPreRelease,

    # Release notes body (inline string). Overrides -NotesFile.
    [string] $Notes,

    # Path to a markdown file used as the release body. Defaults to RELEASE_NOTES.md at the repo root
    # if it exists. When neither -Notes nor a notes file is available, GitHub auto-generates notes
    # from the commits/PRs since the previous tag.
    [string] $NotesFile,

    # Build configuration, passed through to release.ps1.
    [string] $Configuration = 'Release',

    # Optional archive URLs of FFmpeg 8 macOS .dylib files, passed through to release.ps1.
    [string] $OsxX64FFmpegUrl,
    [string] $OsxArm64FFmpegUrl,

    # Show every action without mutating git, GitHub, or (after the build) anything else.
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$propsFile = Join-Path $repoRoot 'Directory.Build.props'
$releaseScript = Join-Path $PSScriptRoot 'release.ps1'
$distRoot = Join-Path $repoRoot 'dist'

function Write-Step([string] $msg) { Write-Host "==> $msg" -ForegroundColor Green }
function Write-Info([string] $msg) { Write-Host "    $msg" }

function Get-BaseVersion {
    $content = Get-Content $propsFile -Raw
    if ($content -match '<VersionPrefix>\s*([^<\s]+)\s*</VersionPrefix>') { return $Matches[1] }
    throw "No <VersionPrefix> found in $propsFile"
}

# ---- Preflight -------------------------------------------------------------------------------
Write-Step 'Preflight checks'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is not on PATH. Install it and run 'gh auth login'."
}
& gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "gh is not authenticated. Run 'gh auth login' first." }
Write-Info 'gh authenticated.'

# Working tree must be clean: release.ps1 will rewrite Directory.Build.props, and we only want that
# single, expected change to be committed for the tag.
$dirty = & git -C $repoRoot status --porcelain
if ($dirty) {
    Write-Host $dirty
    throw "Working tree is not clean. Commit or stash changes before cutting a release."
}
$branch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
Write-Info "Clean tree on branch '$branch'."

if ($Rids -contains 'osx-x64' -or $Rids -contains 'osx-arm64') {
    if (-not ($OsxX64FFmpegUrl -or $OsxArm64FFmpegUrl)) {
        Write-Host "    [warn] macOS RIDs requested without -OsxX64FFmpegUrl / -OsxArm64FFmpegUrl:" -ForegroundColor Yellow
        Write-Host "           those bundles will ship WITHOUT FFmpeg and decode will not work there" -ForegroundColor Yellow
        Write-Host "           (ARCHITECTURE.md §11). You can re-cut macOS later with the URLs supplied." -ForegroundColor Yellow
    }
}

# ---- Build -----------------------------------------------------------------------------------
Write-Step 'Building release bundles (release.ps1)'

# Resolve the prerelease suffix to stamp into the build so it surfaces in the in-app About box and
# the artifact names — matching the suffix carried by the git tag. A full release ($NotPreRelease)
# carries none; an explicit -Tag uses the part after "v<version>-"; otherwise the -PreReleaseLabel.
$versionSuffix = ''
if (-not $NotPreRelease) {
    if ($Tag) {
        if ($Tag -match '^v?\d+\.\d+\.\d+-(.+)$') { $versionSuffix = $Matches[1] }
    } else {
        $versionSuffix = $PreReleaseLabel
    }
}

$releaseArgs = @{
    Rids          = $Rids
    Configuration = $Configuration
}
if ($versionSuffix)    { $releaseArgs.VersionSuffix = $versionSuffix }
if ($NoBump)           { $releaseArgs.NoBump = $true }
if ($OsxX64FFmpegUrl)  { $releaseArgs.OsxX64FFmpegUrl = $OsxX64FFmpegUrl }
if ($OsxArm64FFmpegUrl){ $releaseArgs.OsxArm64FFmpegUrl = $OsxArm64FFmpegUrl }

if ($DryRun) {
    Write-Info "[dry-run] would run: release.ps1 $(($releaseArgs.GetEnumerator() | ForEach-Object { "-$($_.Key) $($_.Value -join ',')" }) -join ' ')"
} else {
    & $releaseScript @releaseArgs
    if ($LASTEXITCODE -ne 0) { throw "release.ps1 failed (exit $LASTEXITCODE)." }
}

# Resolve the built version and the tag now that the (possible) bump has been written.
$version = Get-BaseVersion
if (-not $Tag) {
    $Tag = "v$version-$PreReleaseLabel"
}
Write-Info "Version: $version   Tag: $Tag"

# Collect the artifacts to attach.
$assets = @()
if (-not $DryRun) {
    $assets = Get-ChildItem -Path $distRoot -Filter '*.zip' -File | Select-Object -ExpandProperty FullName
    if (-not $assets) { throw "No .zip artifacts found in $distRoot." }
    Write-Info "Artifacts: $($assets.Count) zip(s)."
}

# ---- Commit the version bump -----------------------------------------------------------------
$bumpPending = & git -C $repoRoot status --porcelain $propsFile
if ($bumpPending) {
    Write-Step "Committing version bump ($version)"
    if ($DryRun) {
        Write-Info "[dry-run] would: git add Directory.Build.props && git commit -m 'chore: release $Tag'"
    } else {
        & git -C $repoRoot add $propsFile
        & git -C $repoRoot commit -m "chore: release $Tag"
        if ($LASTEXITCODE -ne 0) { throw "git commit failed." }
    }
} else {
    Write-Info 'No version-bump change to commit (NoBump or already committed).'
}

# ---- Tag -------------------------------------------------------------------------------------
Write-Step "Tagging $Tag"
$existingTag = & git -C $repoRoot tag --list $Tag
if ($existingTag) { throw "Tag '$Tag' already exists. Choose another tag or delete it first." }
if ($DryRun) {
    Write-Info "[dry-run] would: git tag -a $Tag -m 'Sprocket $Tag'"
} else {
    & git -C $repoRoot tag -a $Tag -m "Sprocket $Tag"
    if ($LASTEXITCODE -ne 0) { throw "git tag failed." }
}

# ---- Push branch + tag -----------------------------------------------------------------------
Write-Step "Pushing '$branch' and tag '$Tag' to origin"
if ($DryRun) {
    Write-Info "[dry-run] would: git push origin $branch && git push origin $Tag"
} else {
    & git -C $repoRoot push origin $branch
    if ($LASTEXITCODE -ne 0) { throw "git push (branch) failed." }
    & git -C $repoRoot push origin $Tag
    if ($LASTEXITCODE -ne 0) { throw "git push (tag) failed." }
}

# ---- Create the GitHub release ---------------------------------------------------------------
Write-Step "Creating GitHub release $Tag"
$ghArgs = @('release', 'create', $Tag, '--title', $Tag, '--verify-tag')
if (-not $NotPreRelease) { $ghArgs += '--prerelease' }

# Notes source precedence: inline -Notes > -NotesFile > repo-root RELEASE_NOTES.md > auto-generated.
if (-not $NotesFile) {
    $defaultNotes = Join-Path $repoRoot 'RELEASE_NOTES.md'
    if (Test-Path $defaultNotes) { $NotesFile = $defaultNotes }
}
if ($Notes) {
    $ghArgs += @('--notes', $Notes)
    Write-Info 'Notes: inline -Notes.'
} elseif ($NotesFile) {
    if (-not (Test-Path $NotesFile)) { throw "Notes file not found: $NotesFile" }
    $ghArgs += @('--notes-file', $NotesFile)
    Write-Info "Notes: $([System.IO.Path]::GetFileName($NotesFile))."
} else {
    $ghArgs += '--generate-notes'
    Write-Info 'Notes: GitHub auto-generated (no RELEASE_NOTES.md found).'
}
$ghArgs += $assets

if ($DryRun) {
    Write-Info "[dry-run] would: gh $($ghArgs -join ' ')"
} else {
    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
}

Write-Host ""
Write-Host "Done. Released $Tag$(if (-not $NotPreRelease) { ' (prerelease)' })." -ForegroundColor Cyan
if (-not $DryRun) {
    & gh release view $Tag --web 2>$null
}
