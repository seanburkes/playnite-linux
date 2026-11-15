#!/usr/bin/env bash
# Materialise the base policy scripts and emit the selected paths.
# Usage: materialise_policy.sh <base-sha> <runner-temp> <github-output>
set -euo pipefail

base="${1:-}"
runner_temp="${2:-}"
output="${3:-}"
if [ -z "$runner_temp" ] || [ -z "$output" ]; then
  echo "usage: $0 <base-sha> <runner-temp> <github-output>" >&2
  exit 2
fi

root="$runner_temp/base-policy"
archived=0
if [ -n "$base" ] && { \
  git cat-file -e "$base:scripts/ci/check_linux_test_results.py" 2>/dev/null || \
  git cat-file -e "$base:scripts/ci/check_protected_paths.py" 2>/dev/null || \
  git cat-file -e "$base:scripts/ci/capture_policy_state.sh" 2>/dev/null || \
  git cat-file -e "$base:scripts/ci/list_test_projects.py" 2>/dev/null; }; then
  rm -rf "$root"
  mkdir -p "$root"
  git archive --format=tar "$base" scripts/ci | tar -x -C "$root"
  archived=1
fi

for spec in "checker:check_linux_test_results.py" \
            "gate:check_protected_paths.py" \
            "lister:list_test_projects.py" \
            "capture:capture_policy_state.sh"; do
  key=${spec%%:*}
  script=${spec#*:}
  if [ -n "$base" ] && git cat-file -e "$base:scripts/ci/$script" 2>/dev/null; then
    if [ "$archived" -ne 1 ] || [ ! -f "$root/scripts/ci/$script" ]; then
      echo "::error::Base commit contains scripts/ci/$script but it could not be materialised"
      exit 1
    fi
    printf '%s=%s\n' "$key" "$root/scripts/ci/$script" >> "$output"
    printf '%s_source=base\n' "$key" >> "$output"
  else
    echo "::warning::BOOTSTRAP: base commit has no scripts/ci/$script; running the head copy for this transition only"
    printf '%s=%s\n' "$key" "scripts/ci/$script" >> "$output"
    printf '%s_source=head\n' "$key" >> "$output"
  fi
done
