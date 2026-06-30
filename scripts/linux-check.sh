#!/usr/bin/env bash
#
# Sprocket Linux verification harness (headless).
# Builds Sprocket.App from source on Linux, fetches a bundled FFmpeg 8 shared build, and exercises the
# hand-rolled FFmpeg-8 binding end-to-end: the version probe (proves the natives resolve + are FFmpeg 8)
# and a real decode (--probe opens a clip through MediaSource and reports dimensions / hw-or-sw). This
# proves the media stack works cross-platform on the SAME managed code that ships (no Sdcb, no spike).
#
# Render parity (decode -> SkSL -> deterministic pixels) is covered cross-platform by `dotnet test`
# (Sprocket.Render.Tests / Sprocket.Export.Tests run the real SkSL on an offscreen surface on Linux too).
#
# Run from the repo root on a machine with Docker:
#   docker run --rm \
#     -v "$PWD:/repo" -v "$PWD/scripts:/scripts:ro" -e HOME=/root \
#     mcr.microsoft.com/dotnet/sdk:10.0 bash /scripts/linux-check.sh
#
# Expected tail: "[probe] OK: ..." with avcodec 62 reported by the ffmpeg-check.
#
set -euo pipefail

echo "== distro =="
grep PRETTY_NAME /etc/os-release

echo "== installing runtime deps =="
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq xz-utils curl ca-certificates \
    libva2 libva-drm2 libvdpau1 libdrm2 libxml2 >/dev/null
echo "deps installed"

echo "== fetching FFmpeg 8 shared build (bundled, not distro) =="
mkdir -p /opt/ff && cd /opt/ff
URL=$(curl -sL https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest \
  | grep browser_download_url \
  | grep linux64-gpl-shared \
  | grep -v '\.sha256' \
  | grep -oE 'https[^"]*' \
  | grep -E 'n8\.' | head -1)
echo "asset: $URL"
curl -sL -o ff.tar.xz "$URL"
tar xf ff.tar.xz --strip-components=1
echo "ffmpeg libs:"; ls lib/ | grep -E 'libav(codec|format|util)\.so' || true
# The hand-rolled binding's FFmpegLoader resolves natives from %SPROCKET_FFMPEG8_DIR% (and LD_LIBRARY_PATH
# as a belt-and-suspenders); the bundled ffmpeg CLI on PATH generates the test clip.
export SPROCKET_FFMPEG8_DIR=/opt/ff/lib
export LD_LIBRARY_PATH=/opt/ff/lib
export PATH=/opt/ff/bin:$PATH

echo "== build app (copied into container fs to avoid Windows obj clash) =="
mkdir -p /work && cp -r /repo/src /repo/Directory.Build.props /repo/Sprocket.slnx /work/ 2>/dev/null || cp -r /repo/src /work/
cd /work && rm -rf src/*/bin src/*/obj
dotnet build src/Sprocket.App/Sprocket.App.csproj -c Release -v q -nologo
echo "build done"
APPDLL=/work/src/Sprocket.App/bin/Release/net10.0/Sprocket.dll

echo "== ffmpeg-check (binding resolves FFmpeg 8 natives) =="
dotnet "$APPDLL" --ffmpeg-check

echo "== generate a sample clip + probe a real decode =="
ffmpeg -y -f lavfi -i testsrc2=size=320x240:rate=30:duration=1 -pix_fmt yuv420p -c:v libx264 /work/sample.mp4 >/dev/null 2>&1
dotnet "$APPDLL" --probe /work/sample.mp4
RC=$?
exit $RC
