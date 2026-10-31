#!/bin/bash
# Updates this repository, builds the mod against the server's own BepInEx and installs it.
# Everything runs in containers (alpine/git for git, .NET SDK for the build), so the host needs only Docker.
#
# Usage: tools/deploy.sh /path/to/server
#   /path/to/server is the folder with VRisingServer.exe and BepInEx/.
# After it finishes, restart the server.
#
# Private repository: put a read-only deploy key into ../.caravan-deploy/id_ed25519 (next to this
# repository's folder); git then uses it. Without that folder git works without a key.
set -euo pipefail

SERVER="${1:?usage: tools/deploy.sh /path/to/server}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
KEYS="$(dirname "$REPO")/.caravan-deploy"
SDK=mcr.microsoft.com/dotnet/sdk:6.0

echo "== git pull"
GIT=(docker run --rm --memory=512m -v "$REPO":/src -w /src)
if [ -f "$KEYS/id_ed25519" ]; then
  GIT+=(-v "$KEYS":/deploy -e "GIT_SSH_COMMAND=ssh -i /deploy/id_ed25519 -o UserKnownHostsFile=/deploy/known_hosts -o StrictHostKeyChecking=accept-new")
fi
"${GIT[@]}" alpine/git -c safe.directory=/src pull --ff-only

echo "== build"
docker run --rm --memory=2g -v "$REPO":/src -v "$SERVER/BepInEx":/bep:ro -w /src "$SDK" \
  dotnet build -c Release -p:BepDir=/bep

echo "== install"
mkdir -p "$SERVER/BepInEx/plugins/CaravanTracker"
cp "$REPO/bin/Release/net6.0/CaravanTracker.dll" "$SERVER/BepInEx/plugins/CaravanTracker/"
chown -R --reference="$SERVER/winhttp.dll" "$SERVER/BepInEx/plugins/CaravanTracker"
grep -m1 -o 'PLUGIN_VERSION = "[^"]*"' "$REPO/Plugin.cs"
echo "Installed. Restart the server."
