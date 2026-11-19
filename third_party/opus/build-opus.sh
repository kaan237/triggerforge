#!/usr/bin/env bash
# Rebuilds third_party/opus/win-x64/opus.dll from the official Xiph source release.
# Needs Visual Studio 2026 (C++ workload, bundled CMake). Run from Git Bash:
#   bash third_party/opus/build-opus.sh
set -euo pipefail

VERSION=1.6.1
SHA256=6ffcb593207be92584df15b32466ed64bbec99109f007c82205f0194572411a1
URL="https://downloads.xiph.org/releases/opus/opus-$VERSION.tar.gz"

here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

vs="/c/Program Files/Microsoft Visual Studio/18/Community"
cmake="$vs/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe"

curl -sSL -4 -o "$work/opus.tar.gz" "$URL"
echo "$SHA256  $work/opus.tar.gz" | sha256sum -c -
tar xzf "$work/opus.tar.gz" -C "$work"

"$cmake" -S "$work/opus-$VERSION" -B "$work/build" -A x64 \
  -DBUILD_SHARED_LIBS=ON -DOPUS_BUILD_TESTING=OFF -DOPUS_BUILD_PROGRAMS=OFF \
  -DOPUS_STATIC_RUNTIME=ON
"$cmake" --build "$work/build" --config Release

mkdir -p "$here/win-x64"
cp "$work/build/Release/opus.dll" "$here/win-x64/opus.dll"
cp "$work/opus-$VERSION/COPYING" "$here/COPYING"
sha256sum "$here/win-x64/opus.dll"
