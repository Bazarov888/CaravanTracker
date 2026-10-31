#!/bin/bash
# Updates this repository, builds the mod against the server's own BepInEx and installs it.
# Everything runs in the .NET SDK container, so the host needs only Docker.
#
# Usage: tools/deploy.sh /path/to/server
#   /path/to/server is the folder with VRisingServer.exe and BepInEx/.
# After it finishes, restart the server.
set -euo pipefail

SERVER="${1:?usage: tools/deploy.sh /path/to/server}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
IMAGE=mcr.microsoft.com/dotnet/sdk:6.0

echo "== git pull"
docker run --rm --memory=1g -v "$REPO":/src -w /src "$IMAGE" \
  sh -c 'git config --global --add safe.directory /src && git pull --ff-only'

echo "== build"
docker run --rm --memory=2g -v "$REPO":/src -v "$SERVER/BepInEx":/bep:ro -w /src "$IMAGE" \
  dotnet build -c Release -p:BepDir=/bep

echo "== install"
mkdir -p "$SERVER/BepInEx/plugins/CaravanTracker"
cp "$REPO/bin/Release/net6.0/CaravanTracker.dll" "$SERVER/BepInEx/plugins/CaravanTracker/"
chown -R --reference="$SERVER/winhttp.dll" "$SERVER/BepInEx/plugins/CaravanTracker"
grep -m1 -o 'PLUGIN_VERSION = "[^"]*"' "$REPO/Plugin.cs"
echo "Installed. Restart the server."
