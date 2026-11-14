#!/bin/sh
# Emit the sandbox's own identity, then run the existing host-child probe.
# The probe requires Python 3 and /proc, both present in org.freedesktop.Platform.
set -eu
printf 'sandbox_identity pid=%s namespace=%s\n' "$$" "$(readlink /proc/self/ns/pid)"
exec /usr/bin/python3 -u /app/bin/probe.py --host-child
