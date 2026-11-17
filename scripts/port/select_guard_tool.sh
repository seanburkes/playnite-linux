#!/usr/bin/env bash
# Select the generator that decides generated-path ownership.
#
# The decision must use the newest rules available, never a stale PR's old
# tool and never the PR event base's tool. If the PR head contains the current
# main tip, the head tool already carries main's rules (plus any reviewed PR
# change). If the head does not contain current main, the current-main tool is
# materialised inside the checkout so the generator still resolves its own
# repository root, and the stale head tool is not trusted.
#
# Output: two shell assignments on stdout:
#   PORT_GUARD_TOOL=<path>
#   PORT_GUARD_MAIN_TIP=<sha>
# Tests may pass MAIN_TIP_OVERRIDE instead of fetching origin/main.
set -euo pipefail

root="${REPO_ROOT:-$PWD}"
authorized="${PORT_TOOL_CHANGE_AUTHORIZED:-false}"
main_tip="${MAIN_TIP_OVERRIDE:-}"

if [ -z "$main_tip" ]; then
  if ! git -C "$root" fetch --quiet --no-tags origin main; then
    echo "::error::Cannot fetch the current main tip; refusing to decide generated-path ownership against the PR event base" >&2
    exit 1
  fi
  main_tip=$(git -C "$root" rev-parse --verify FETCH_HEAD^{commit})
fi
if ! printf '%s' "$main_tip" | grep -Eq '^[0-9a-f]{40}$' ||
   ! git -C "$root" cat-file -e "$main_tip^{commit}" 2>/dev/null; then
  echo "::error::Current main tip '$main_tip' is not a commit in this checkout" >&2
  exit 1
fi

head_tool="$root/scripts/port/build_port_index.py"
if git -C "$root" merge-base --is-ancestor "$main_tip" HEAD; then
  if [ ! -f "$head_tool" ]; then
    echo "::error::$head_tool is missing at head; the guard cannot run head rules" >&2
    exit 1
  fi
  if git -C "$root" cat-file -e "$main_tip:scripts/port/build_port_index.py" 2>/dev/null &&
     ! git -C "$root" diff --quiet "$main_tip" -- scripts/port/build_port_index.py; then
    if [ "$authorized" != "true" ]; then
      echo "::error::scripts/port/build_port_index.py differs from current main $main_tip; this gate runs the head rules and a generator change requires the oracle-change label" >&2
      exit 1
    fi
    echo "::notice::generator differs from current main; running the authorised head tool" >&2
  fi
  tool="$head_tool"
else
  if git -C "$root" cat-file -e "$main_tip:scripts/port/build_port_index.py" 2>/dev/null; then
    tool="$root/scripts/port/.guard_build_port_index.py"
    git -C "$root" show "$main_tip:scripts/port/build_port_index.py" > "$tool"
    echo "::notice::PR head does not contain current main $main_tip; using current-main rules instead of the stale head tool" >&2
  elif [ -f "$head_tool" ]; then
    echo "::warning::current main has no scripts/port/build_port_index.py; falling back to the head tool" >&2
    tool="$head_tool"
  else
    echo "::error::Neither current main nor the PR head has scripts/port/build_port_index.py" >&2
    exit 1
  fi
fi

printf 'PORT_GUARD_TOOL=%q\n' "$tool"
printf 'PORT_GUARD_MAIN_TIP=%q\n' "$main_tip"
