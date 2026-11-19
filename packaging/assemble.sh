#!/usr/bin/env bash
set -euo pipefail
repo=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
test -z "$(git -C "$repo" status --porcelain)" || { echo 'Packaging requires a clean committed checkout' >&2; exit 1; }
commit=$(git -C "$repo" rev-parse HEAD)
out=${1:?Usage: assemble.sh ABSOLUTE_OUTPUT_DIRECTORY}
[[ "$out" = /* && ! -e "$out" ]] || { echo 'Output must be an absolute, new directory' >&2; exit 1; }
mkdir -p "$out/source" "$out/payload/lib/playnite"
# Publish from an isolated source export: RID-specific restore never rewrites repository locks.
git -C "$repo" archive "$commit" | tar -x -C "$out/source"
inputs="$out/source/packaging"
printf '%s\n' "$commit" > "$out/source-commit.txt"
test "$(dotnet --version)" = "$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sdk"]["version"])' "$out/source/global.json")"
for project in Cli Desktop; do
    dotnet publish "$out/source/src/Playnite.$project/Playnite.$project.csproj" \
        -c Release -r linux-x64 --self-contained true \
        -p:RuntimeFrameworkVersion=11.0.0 -p:RestoreLockedMode=false \
        -o "$out/payload/lib/playnite/${project,,}"
done
install -Dm755 "$inputs/playnite" "$out/payload/bin/playnite"
id=io.github.playnite_linux.Playnite
install -Dm644 "$inputs/$id.desktop" "$out/payload/share/applications/$id.desktop"
install -Dm644 "$inputs/$id.svg" "$out/payload/share/icons/hicolor/scalable/apps/$id.svg"
mkdir -p "$out/AppDir/usr"
cp -a "$out/payload/." "$out/AppDir/usr/"
dotnet publish "$out/source/tools/p5-008/SteamAcceptance/SteamAcceptance.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -p:RuntimeFrameworkVersion=11.0.0 -p:RestoreLockedMode=false \
    -o "$out/AppDir/usr/lib/playnite/steam-acceptance"
install -m755 "$inputs/AppRun" "$out/AppDir/AppRun"
ln -s "usr/share/applications/$id.desktop" "$out/AppDir/$id.desktop"
ln -s "usr/share/icons/hicolor/scalable/apps/$id.svg" "$out/AppDir/$id.svg"
ln -s "$id.svg" "$out/AppDir/.DirIcon"
cp "$inputs/$id.json" "$out/$id.json"
