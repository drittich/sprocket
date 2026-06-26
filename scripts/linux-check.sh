#!/usr/bin/env bash
#
# Sprocket Linux verification harness (headless).
# Builds the spike on Linux, decodes via bundled FFmpeg 7, runs the SkSL brightness shader,
# and renders an offscreen PNG — proving the media + Skia stack works cross-platform.
#
# Run from the repo root on a machine with Docker:
#   docker run --rm \
#     -v "$PWD:/repo" -v "$PWD/scripts:/scripts:ro" -e HOME=/root \
#     mcr.microsoft.com/dotnet/sdk:10.0 bash /scripts/linux-check.sh
#
# Expected tail: "[headless] RESULT: PASS".
#
set -euo pipefail

echo "== distro =="
grep PRETTY_NAME /etc/os-release

echo "== installing runtime deps =="
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq libfontconfig1 xz-utils curl ca-certificates \
    libva2 libva-drm2 libvdpau1 libdrm2 libxml2 >/dev/null
echo "deps installed"

echo "== fetching FFmpeg 7 shared build (bundled, not distro) =="
mkdir -p /opt/ff && cd /opt/ff
URL=$(curl -sL https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest \
  | grep browser_download_url \
  | grep linux64-gpl-shared \
  | grep -v '\.sha256' \
  | grep -oE 'https[^"]*' \
  | grep -E 'n7\.' | head -1)
echo "asset: $URL"
curl -sL -o ff.tar.xz "$URL"
tar xf ff.tar.xz --strip-components=1
echo "ffmpeg libs:"; ls lib/ | grep -E 'libav(codec|format|util)\.so' || true
export LD_LIBRARY_PATH=/opt/ff/lib

echo "== build project (copied into container fs to avoid Windows obj clash) =="
mkdir -p /work && cp -r /repo/src/Sprocket.Spike /work/proj
cd /work/proj && rm -rf bin obj
dotnet build -c Release -v q -nologo
echo "build done"

echo "== run headless check =="
dotnet bin/Release/net10.0/Sprocket.Spike.dll --headless-check
RC=$?
cp -f bin/Release/net10.0/headless-out.png /repo/linux-headless-out.png 2>/dev/null || true
exit $RC
