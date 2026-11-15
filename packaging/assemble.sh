#!/usr/bin/env bash
set -euo pipefail
repo=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
out=${1:?Usage: assemble.sh ABSOLUTE_OUTPUT_DIRECTORY}
[[ "$out" = /* && ! -e "$out" ]] || { echo 'Output must be an absolute, new directory' >&2; exit 1; }
mkdir -p "$out/source" "$out/payload/lib/playnite"
# Publish from an isolated source export: RID-specific restore never rewrites repository locks.
git -C "$repo" archive HEAD | tar -x -C "$out/source"
test "$(dotnet --version)" = "$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sdk"]["version"])' "$repo/global.json")"
for project in Cli Desktop; do
    dotnet publish "$out/source/src/Playnite.$project/Playnite.$project.csproj" \
        -c Release -r linux-x64 --self-contained true \
        -p:RuntimeFrameworkVersion=11.0.0 -p:RestoreLockedMode=false \
        -o "$out/payload/lib/playnite/${project,,}"
done
install -Dm755 "$repo/packaging/playnite" "$out/payload/bin/playnite"
id=io.github.playnite_linux.Playnite
install -Dm644 "$repo/packaging/$id.desktop" "$out/payload/share/applications/$id.desktop"
install -Dm644 "$repo/packaging/$id.svg" "$out/payload/share/icons/hicolor/scalable/apps/$id.svg"
mkdir -p "$out/AppDir/usr"
cp -a "$out/payload/." "$out/AppDir/usr/"
install -m755 "$repo/packaging/AppRun" "$out/AppDir/AppRun"
ln -s "usr/share/applications/$id.desktop" "$out/AppDir/$id.desktop"
ln -s "usr/share/icons/hicolor/scalable/apps/$id.svg" "$out/AppDir/$id.svg"
ln -s "$id.svg" "$out/AppDir/.DirIcon"
cp "$repo/packaging/$id.json" "$out/$id.json"
