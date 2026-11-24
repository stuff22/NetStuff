#!/bin/bash
# Baut Net-Stuff.app für macOS (Apple Silicon oder Intel, je nach Rechner).
# Nur als .app-Bundle kann macOS die Standortfreigabe erteilen – ohne sie bleiben WLAN-Namen verborgen.
#   Aufruf im Repository-Ordner:  bash Desktop/macos/build-app.sh
#   Ergebnis:                     Desktop/bin/app/Net-Stuff.app  (ad-hoc signiert)
set -euo pipefail
cd "$(dirname "$0")/../.."

RID=osx-arm64
[ "$(uname -m)" = "x86_64" ] && RID=osx-x64

OUT=Desktop/bin/app
FINAL="$OUT/Net-Stuff.app"
rm -rf "$OUT"
mkdir -p "$OUT"

# Bundle in einem temporären Ordner zusammenbauen: Im Projektordner (z. B. iCloud-Dokumente) hängt macOS
# Finder-/iCloud-Attribute an, und codesign bricht dann mit „resource fork … detritus not allowed“ ab.
WORK=$(mktemp -d "${TMPDIR:-/tmp}/netstuff-app.XXXXXX")
trap 'rm -rf "$WORK"' EXIT
APP="$WORK/Net-Stuff.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

dotnet publish Desktop -c Release -r "$RID" --self-contained true -p:UseAppHost=true -o "$OUT/publish"
cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
cp Desktop/macos/Info.plist "$APP/Contents/Info.plist"

# Symbol aus dem PNG erzeugen (falls vorhanden)
PNG=$(ls Assets/*.png 2>/dev/null | head -1 || true)
if [ -n "$PNG" ]; then
    SET="$WORK/netstuff.iconset"
    mkdir -p "$SET"
    for s in 16 32 128 256 512; do
        sips -z $s $s "$PNG" --out "$SET/icon_${s}x${s}.png" >/dev/null
        sips -z $((s*2)) $((s*2)) "$PNG" --out "$SET/icon_${s}x${s}@2x.png" >/dev/null
    done
    iconutil -c icns "$SET" -o "$APP/Contents/Resources/netstuff.icns" || true
fi

xattr -cr "$APP"
codesign --force --deep --sign - "$APP"
codesign --verify --deep "$APP"
ditto --norsrc --noextattr --noqtn "$APP" "$FINAL"
rm -rf "$OUT/publish"
echo
echo "Fertig: $FINAL"
echo "Starten mit:  open \"$FINAL\""
