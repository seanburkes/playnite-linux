#!/usr/bin/env bash
set -euo pipefail
root="$(git -C "$(dirname "${BASH_SOURCE[0]}")" rev-parse --show-toplevel)"
exec dotnet run --project "$root/tools/p5-008/SteamAcceptance/SteamAcceptance.csproj" --configuration Release -- "$@"
