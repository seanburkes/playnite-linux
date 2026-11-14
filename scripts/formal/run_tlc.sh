#!/usr/bin/env bash
# Check all TLA+ models with TLC.
# Requires java and a tla2tools.jar. Set TLA2TOOLS_JAR to override the default.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
FORMAL="$ROOT/docs/formal"
JAR="${TLA2TOOLS_JAR:-/tmp/opencode/tla/tla2tools.jar}"

if [[ ! -f "$JAR" ]]; then
  echo "tla2tools.jar not found at $JAR" >&2
  echo "Download: https://github.com/tlaplus/tlaplus/releases/latest/download/tla2tools.jar" >&2
  exit 1
fi

status=0
for model in LibraryImport SettingsMigration GameSession; do
  echo "== $model =="
  if ! java -Xmx2g -cp "$JAR" tlc2.TLC \
      -config "$FORMAL/$model.cfg" \
      -deadlock \
      -metadir "/tmp/opencode/tla/states-$model" \
      "$FORMAL/$model.tla"; then
    status=1
  fi
  echo
done

exit "$status"
