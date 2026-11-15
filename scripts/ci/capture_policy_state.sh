#!/usr/bin/env bash
# Capture the committed policy state from the comparison base.
# Usage: capture_policy_state.sh <base-sha> <runner-temp> <github-output> <lister>
set -euo pipefail

base="${1:-}"
runner_temp="${2:-}"
output="${3:-}"
lister="${4:-}"
if [ -z "$runner_temp" ] || [ -z "$output" ] || [ -z "$lister" ]; then
  echo "usage: $0 <base-sha> <runner-temp> <github-output> <lister>" >&2
  exit 2
fi

if [ -z "$base" ]; then
  echo "::notice::No base commit for this first push; first-baseline policy applies"
  printf 'baseline_state=absent\nprojects_state=absent\n' >> "$output"
  exit 0
fi

if ! git cat-file -e "$base:tests/baseline.json" 2>/dev/null; then
  echo "::error::Base commit $base has no tests/baseline.json; refusing to open the ratchet"
  exit 1
fi
git show "$base:tests/baseline.json" > "$runner_temp/previous-baseline.json"
if [ ! -s "$runner_temp/previous-baseline.json" ]; then
  echo "::error::Base commit $base has an empty or unreadable tests/baseline.json"
  exit 1
fi
printf 'baseline_state=present\n' >> "$output"

python3 "$lister" --ref "$base" > "$runner_temp/previous-projects.txt"
printf 'projects_state=present\n' >> "$output"
