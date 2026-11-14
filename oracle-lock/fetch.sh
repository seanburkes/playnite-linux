#!/usr/bin/env bash
# Fetch and verify the pinned Playnite.Deps package used by the Windows oracle.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="$ROOT/oracle-lock/packages"
URL="https://nuget.playnite.link/v3/package/playnite.deps/10.56.0/playnite.deps.10.56.0.nupkg"
FILE="Playnite.Deps.10.56.0.nupkg"

mkdir -p "$DEST"
if [[ ! -f "$DEST/$FILE" ]]; then
  echo "fetching $URL"
  curl -fsSL --retry 3 --max-time 300 -o "$DEST/$FILE" "$URL"
fi

cd "$ROOT/oracle-lock"
if ! sha256sum -c SHA256SUMS; then
  echo "checksum mismatch: refusing to use the cached package" >&2
  rm -f "$DEST/$FILE"
  exit 1
fi

echo "ok: $DEST/$FILE"
