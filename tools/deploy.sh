#!/bin/bash
# Updates this repository, builds the mod against the server's own BepInEx and installs it.
# Everything runs in containers (alpine/git for git, .NET SDK for the build), so the host needs only Docker.
#
# Usage: tools/deploy.sh /path/to/server
#   /path/to/server is the folder with VRisingServer.exe and BepInEx/.
# After it finishes, restart the server.
#
# A clone over SSH (or of a private fork) needs a key: put a read-only deploy key into
# ../.caravan-deploy/id_ed25519 (next to this repository's folder) and git uses it. Without that folder
# git works without a key, which is enough for a public repository cloned over HTTPS.
#
# The whole script is one function called on the last line: bash reads it completely before running it,
# so a git pull that changes this very file can't break the run in progress.
set -euo pipefail

# alpine/git pinned by digest (tag 2.54.0), so a changed image can't run on the server unnoticed.
GIT_IMAGE=alpine/git:2.54.0@sha256:e8ceedc73bd80992ae8d58a7d24c58b8322146c354f1d2e81a97db4452068ec0
SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:6.0

main() {
  local server="${1:?usage: tools/deploy.sh /path/to/server}"
  local repo keys
  repo="$(cd "$(dirname "$0")/.." && pwd)"
  keys="$(dirname "$repo")/.caravan-deploy"

  echo "== git pull"
  local git=(docker run --rm --memory=512m -v "$repo":/src -w /src)
  if [ -f "$keys/id_ed25519" ]; then
    git+=(-v "$keys":/deploy -e "GIT_SSH_COMMAND=ssh -i /deploy/id_ed25519 -o UserKnownHostsFile=/deploy/known_hosts -o StrictHostKeyChecking=accept-new")
  fi
  "${git[@]}" "$GIT_IMAGE" -c safe.directory=/src pull --ff-only

  echo "== build"
  docker run --rm --memory=2g -v "$repo":/src -v "$server/BepInEx":/bep:ro -w /src "$SDK_IMAGE" \
    dotnet build -c Release -p:BepDir=/bep

  echo "== install"
  mkdir -p "$server/BepInEx/plugins/CaravanTracker"
  cp "$repo/bin/Release/net6.0/CaravanTracker.dll" "$server/BepInEx/plugins/CaravanTracker/"
  chown -R --reference="$server/winhttp.dll" "$server/BepInEx/plugins/CaravanTracker"
  echo "Installed CaravanTracker $(grep -m1 -o '<Version>[^<]*' "$repo/CaravanTracker.csproj" | cut -d'>' -f2). Restart the server."
}

main "$@"; exit
