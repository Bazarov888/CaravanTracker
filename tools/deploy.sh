#!/bin/bash
# Updates this repository, builds the mod against the server's own BepInEx and installs it.
# Everything runs in containers (alpine/git for git, .NET SDK for the build), so the host needs only Docker.
#
# Usage: tools/deploy.sh /path/to/server
#   /path/to/server is the folder with VRisingServer.exe and BepInEx/.
# After it finishes, restart the server.
#
# A clone over SSH (or of a private fork) needs a key: put a read-only deploy key into
# ../.carriage-deploy/id_ed25519 (next to this repository's folder; the old name ../.caravan-deploy works too)
# and git uses it. Without that folder git works without a key, which is enough for a public repository
# cloned over HTTPS.
#
# Until 1.0.11 the mod was called CaravanTracker; its folder BepInEx/plugins/CaravanTracker is removed here,
# so the server doesn't load the old copy next to the new one (the new one would refuse to start then).
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
  # A mistyped server path would make Docker create empty folders there and the build fail with hundreds of errors.
  if [ ! -d "$server/BepInEx/core" ]; then
    echo "No BepInEx/core in $server: check the server path." >&2
    exit 1
  fi
  keys="$(dirname "$repo")/.carriage-deploy"
  [ -d "$keys" ] || keys="$(dirname "$repo")/.caravan-deploy"

  local git=(docker run --rm --memory=512m -v "$repo":/src -w /src)
  if [ -f "$keys/id_ed25519" ]; then
    git+=(-v "$keys":/deploy -e "GIT_SSH_COMMAND=ssh -i /deploy/id_ed25519 -o UserKnownHostsFile=/deploy/known_hosts -o StrictHostKeyChecking=accept-new")
  fi
  git+=("$GIT_IMAGE" -c safe.directory=/src)

  echo "== git pull"
  # Local edits to tracked files would be built and installed unnoticed: stop instead.
  if [ -n "$("${git[@]}" status --porcelain --untracked-files=no)" ]; then
    echo "The repository has local changes (git status), not deploying." >&2
    exit 1
  fi
  "${git[@]}" pull --ff-only
  local commit
  commit="$("${git[@]}" rev-parse --short HEAD)"

  echo "== build $commit"
  docker run --rm --memory=2g -v "$repo":/src -v "$server/BepInEx":/bep:ro -w /src "$SDK_IMAGE" \
    dotnet build -c Release -p:BepDir=/bep

  echo "== install"
  local plugins="$server/BepInEx/plugins"
  mkdir -p "$plugins/CarriageTracker"
  # Copy next to the old file, then rename over it: the server never sees a half-written DLL.
  cp "$repo/bin/Release/net6.0/CarriageTracker.dll" "$plugins/CarriageTracker/CarriageTracker.dll.new"
  mv -f "$plugins/CarriageTracker/CarriageTracker.dll.new" "$plugins/CarriageTracker/CarriageTracker.dll"
  chown -R --reference="$server/winhttp.dll" "$plugins/CarriageTracker"
  if [ -d "$plugins/CaravanTracker" ]; then
    rm -rf "$plugins/CaravanTracker"
    echo "Removed the old copy BepInEx/plugins/CaravanTracker (the mod's name until 1.0.11)."
  fi
  echo "Installed CarriageTracker $(grep -m1 -o '<Version>[^<]*' "$repo/CarriageTracker.csproj" | cut -d'>' -f2), commit $commit. Restart the server."
}

main "$@"; exit
