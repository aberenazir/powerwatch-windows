#!/bin/bash
# Build PowerWatch and publish it as a GitHub release, so "Check for updates" in the app finds it.
# 1. Raise the version in source/Core.cs (AppInfo.Version) and source/PowerWatch.csproj (<Version>).
# 2. ./release.sh "First change|Second change"      (DOTNET=/path/to/dotnet if it's not on PATH)
set -euo pipefail
cd "$(dirname "$0")"
NOTES="${1:?give the notes, e.g. ./release.sh \"Faster pop-up|Fixed Enter\"}"
DOTNET="${DOTNET:-dotnet}"
VERSION=$(sed -n 's/.*public const string Version = "\(.*\)";.*/\1/p' source/Core.cs)
grep -q "<Version>$VERSION</Version>" source/PowerWatch.csproj || { echo "PowerWatch.csproj <Version> doesn't match $VERSION"; exit 1; }

rm -rf build dist && mkdir build dist && cp source/* build/
( cd build && DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$DOTNET" build -c Release -v q )
cp build/bin/Release/net48/PowerWatch.exe dist/PowerWatch-Setup.exe
SHA=$(shasum -a 256 dist/PowerWatch-Setup.exe | cut -d' ' -f1)
printf 'version=%s\nsha256=%s\nnotes=%s\n' "$VERSION" "$SHA" "$NOTES" > dist/version.txt
cat dist/version.txt

gh release create "v$VERSION" dist/PowerWatch-Setup.exe dist/version.txt \
  --repo aberenazir/powerwatch-windows --title "Power Watch $VERSION" \
  --notes "$(echo "$NOTES" | tr '|' '\n' | sed 's/^/- /')"
