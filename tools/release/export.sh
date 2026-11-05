#!/usr/bin/env bash
# Builds the zips to share the game: Windows (.exe) and macOS (.app), each with the data folder and
# HOW-TO-PLAY.txt. The .NET runtime is bundled, players need nothing else. Output: game/builds/*.zip
# (gitignored, never committed). Pushing a tag v* runs this in CI (.github/workflows/release.yml)
# and attaches the zips to a GitHub Release; locally it needs the Godot 4.7.2 .NET export templates.
#
#   tools/release/export.sh [version]        e.g. tools/release/export.sh 0.1.0
#   GODOT=/path/to/Godot tools/release/export.sh 0.1.0
set -euo pipefail

VERSION="${1:-$(date +%Y.%m.%d)}"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
BUILDS="$REPO/game/builds"
export PATH="$PATH:/usr/local/share/dotnet"   # appended, so CI's setup-dotnet comes first

# Only the content tracked in git ships (as in CI's fresh checkout): nothing local or untracked in data/ (runner
# saves, experiments) gets into a release, and no macOS ._ files. Careers are never in the repo: they live in the
# user data folder (exports use their own, empty one: game/scripts/CareerStore.cs).
copy_data() {
  mkdir -p "$1"
  (cd "$REPO" && git ls-files -z data | COPYFILE_DISABLE=1 tar --null -T - -cf -) | tar -xf - -C "$1" --strip-components 1
  local untracked
  untracked="$(cd "$REPO" && git ls-files --others --exclude-standard data)"
  [ -z "$untracked" ] || printf 'Left out (not tracked in git):\n%s\n' "$untracked"
}

rm -rf "$BUILDS/windows" "$BUILDS/macos"
mkdir -p "$BUILDS/windows" "$BUILDS/macos"

echo "== Import"
# A fresh checkout has no .godot/ import cache yet; exporting needs it.
"$GODOT" --headless --path "$REPO/game" --import

echo "== Windows"
"$GODOT" --headless --path "$REPO/game" --export-release "Windows" "$BUILDS/windows/BikeparkManager.exe"
copy_data "$BUILDS/windows/data"
cp "$REPO/tools/release/HOW-TO-PLAY.txt" "$BUILDS/windows/"
WIN_ZIP="$BUILDS/BikeparkManager-$VERSION-Windows.zip"
rm -f "$WIN_ZIP"
(cd "$BUILDS/windows" && zip -qr "$WIN_ZIP" .)

echo "== macOS"
APP="$BUILDS/macos/Bikepark Manager.app"
"$GODOT" --headless --path "$REPO/game" --export-release "macOS" "$APP"
copy_data "$APP/Contents/Resources/data"
codesign --force --deep -s - "$APP"   # ad-hoc again: copying the data in broke the signature
cp "$REPO/tools/release/HOW-TO-PLAY.txt" "$BUILDS/macos/"
MAC_ZIP="$BUILDS/BikeparkManager-$VERSION-Mac.zip"
rm -f "$MAC_ZIP"
(cd "$BUILDS/macos" && ditto -c -k --norsrc --keepParent "Bikepark Manager.app" "$MAC_ZIP" && zip -q "$MAC_ZIP" HOW-TO-PLAY.txt)

echo "== Done"
ls -lh "$WIN_ZIP" "$MAC_ZIP"
