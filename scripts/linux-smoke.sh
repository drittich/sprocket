#!/usr/bin/env bash
#
# Sprocket Linux release smoke test (headless).
# Runs an already-published, self-contained linux-x64 bundle on a clean Linux machine and confirms
# the bundled FFmpeg 8 native libraries actually load — with LD_LIBRARY_PATH unset, so resolution
# must come from the application directory via the hand-rolled binding's FFmpegLoader (the bundling
# path a shipped build relies on, ARCHITECTURE.md §11). --ffmpeg-check also enforces the FFmpeg-8
# version guard (libavcodec 62). This proves scripts/release.ps1's FFmpeg bundling end-to-end,
# complementing scripts/linux-check.sh (which builds from source and probes a real decode).
#
# Two steps — build on the host, verify in a container:
#   1) Publish + bundle the linux-x64 release (writes ./dist/Sprocket-<ver>-linux-x64/):
#        pwsh scripts/release.ps1 -Rids linux-x64 -NoZip
#   2) Run this script in a clean .NET runtime-deps container from the repo root:
#        docker run --rm -v "$PWD:/repo" -e HOME=/root \
#          mcr.microsoft.com/dotnet/runtime-deps:10.0 bash /repo/scripts/linux-smoke.sh
#
# A bundle path may be passed as $1 to override auto-discovery.
# Expected tail: "[smoke] RESULT: PASS".
set -euo pipefail

echo "== distro =="
grep PRETTY_NAME /etc/os-release || true

echo "== installing FFmpeg shared-lib runtime deps =="
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
# The BtbN gpl-shared FFmpeg build links these; the self-contained app bundles its own .NET runtime.
apt-get install -y -qq libva2 libva-drm2 libvdpau1 libdrm2 libxml2 >/dev/null
echo "deps installed"

echo "== locating linux-x64 bundle =="
BUNDLE_DIR="${1:-}"
if [[ -z "$BUNDLE_DIR" ]]; then
    for d in /repo/dist/*linux-x64*/; do
        [[ -d "$d" ]] && BUNDLE_DIR="$d" && break
    done
fi
if [[ -z "$BUNDLE_DIR" || ! -d "$BUNDLE_DIR" ]]; then
    echo "[smoke] No linux-x64 bundle found under /repo/dist." >&2
    echo "[smoke] Publish one first:  pwsh scripts/release.ps1 -Rids linux-x64 -NoZip" >&2
    echo "[smoke] RESULT: FAIL"
    exit 1
fi
echo "bundle: $BUNDLE_DIR"

APP="$BUNDLE_DIR/Sprocket.App"
if [[ ! -f "$APP" ]]; then
    echo "[smoke] Executable not found: $APP" >&2
    echo "[smoke] RESULT: FAIL"
    exit 1
fi

echo "== bundled FFmpeg libs next to the executable =="
ls "$BUNDLE_DIR" | grep -E '\.so' || { echo "[smoke] No .so files in bundle" >&2; echo "[smoke] RESULT: FAIL"; exit 1; }

echo "== run headless ffmpeg-check (LD_LIBRARY_PATH unset on purpose) =="
chmod +x "$APP"
unset LD_LIBRARY_PATH
set +e
"$APP" --ffmpeg-check
RC=$?
set -e

if [[ $RC -eq 0 ]]; then
    echo "[smoke] RESULT: PASS"
else
    echo "[smoke] ffmpeg-check exited $RC" >&2
    echo "[smoke] RESULT: FAIL"
fi
exit $RC
