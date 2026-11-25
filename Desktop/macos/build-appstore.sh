#!/bin/bash
# Baut Net-Stuff für den Mac App Store: signierte, sandboxed .app und signiertes .pkg zum Hochladen.
#
#   Aufruf im Repository-Ordner:  bash Desktop/macos/build-appstore.sh
#   Ergebnis:                     Desktop/bin/appstore/Net-Stuff.pkg  → mit der App „Transporter“ hochladen
#
# Voraussetzungen (einmalig, siehe Apple-Developer-Konto):
#   - Zertifikat „Apple Distribution“ (oder „3rd Party Mac Developer Application“) im Schlüsselbund
#   - Zertifikat „Mac Installer Distribution“ (oder „3rd Party Mac Developer Installer“) im Schlüsselbund
#   - Provisioning Profile vom Typ „Mac App Store“ für at.itstuff.netstuff, abgelegt als
#     Desktop/macos/NetStuff_AppStore.provisionprofile (oder Pfad in PROFILE angeben)
#
# Optionale Umgebungsvariablen:
#   APP_SIGN_ID        Name des App-Zertifikats       (Standard: automatisch gesucht)
#   INSTALLER_SIGN_ID  Name des Installer-Zertifikats (Standard: automatisch gesucht)
#   PROFILE            Pfad zum Provisioning Profile
#   RID                osx-arm64 (Standard) oder osx-x64
#
# Vor jedem Upload muss CFBundleVersion in Desktop/macos/Info.plist höher sein als beim letzten Upload.
set -euo pipefail
cd "$(dirname "$0")/../.."

RID=${RID:-osx-arm64}
PROFILE=${PROFILE:-Desktop/macos/NetStuff_AppStore.provisionprofile}
ENTITLEMENTS_SRC=Desktop/macos/NetStuff.entitlements
PLIST_SRC=Desktop/macos/Info.plist
OUT=Desktop/bin/appstore

fail() { echo "FEHLER: $*" >&2; exit 1; }

# dotnet auch finden, wenn es nicht im PATH steht (Standard-Installationsort des .NET-Installers)
command -v dotnet >/dev/null || PATH="/usr/local/share/dotnet:$PATH"
command -v dotnet >/dev/null || fail "dotnet nicht gefunden – .NET SDK installieren (dot.net)."

# --- Zertifikate und Profil prüfen -----------------------------------------------------------
find_identity() { # $1 = Suchmuster, $2 = Policy-Argumente
    security find-identity -v $2 | grep -E "\"($1)" | head -1 | sed -E 's/.*"(.*)"/\1/' || true
}
APP_SIGN_ID=${APP_SIGN_ID:-$(find_identity "Apple Distribution|3rd Party Mac Developer Application" "-p codesigning")}
INSTALLER_SIGN_ID=${INSTALLER_SIGN_ID:-$(find_identity "Mac Installer Distribution|3rd Party Mac Developer Installer" "")}
[ -n "$APP_SIGN_ID" ]       || fail "Kein Zertifikat „Apple Distribution“ im Schlüsselbund (Xcode → Settings → Accounts → Manage Certificates)."
[ -n "$INSTALLER_SIGN_ID" ] || fail "Kein Zertifikat „Mac Installer Distribution“ im Schlüsselbund (Xcode → Settings → Accounts → Manage Certificates)."
[ -f "$PROFILE" ]           || fail "Provisioning Profile fehlt: $PROFILE (developer.apple.com → Profiles → Mac App Store)."

BUNDLE_ID=$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "$PLIST_SRC")
VERSION=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$PLIST_SRC")
BUILD=$(/usr/libexec/PlistBuddy -c "Print :CFBundleVersion" "$PLIST_SRC")

WORK=$(mktemp -d "${TMPDIR:-/tmp}/netstuff-appstore.XXXXXX")
trap 'rm -rf "$WORK"' EXIT

# Berechtigungen aus dem Profil lesen und mit unserer Entitlements-Datei zusammenführen
security cms -D -i "$PROFILE" > "$WORK/profile.plist"
APP_IDENTIFIER=$(/usr/libexec/PlistBuddy -c "Print :Entitlements:com.apple.application-identifier" "$WORK/profile.plist")
TEAM_ID=$(/usr/libexec/PlistBuddy -c "Print :Entitlements:com.apple.developer.team-identifier" "$WORK/profile.plist")
[ "$APP_IDENTIFIER" = "$TEAM_ID.$BUNDLE_ID" ] \
    || fail "Profil gehört zu $APP_IDENTIFIER, die App hat aber die Bundle ID $BUNDLE_ID."

ENTITLEMENTS="$WORK/app.entitlements"
cp "$ENTITLEMENTS_SRC" "$ENTITLEMENTS"
/usr/libexec/PlistBuddy -c "Add :com.apple.application-identifier string $APP_IDENTIFIER" "$ENTITLEMENTS"
/usr/libexec/PlistBuddy -c "Add :com.apple.developer.team-identifier string $TEAM_ID" "$ENTITLEMENTS"

echo "App:        $BUNDLE_ID $VERSION ($BUILD), $RID"
echo "Signatur:   $APP_SIGN_ID"
echo "Installer:  $INSTALLER_SIGN_ID"
echo

# --- Bauen und Bundle zusammensetzen ---------------------------------------------------------
rm -rf "$OUT"
mkdir -p "$OUT"
APP="$WORK/Net-Stuff.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

dotnet publish Desktop -c Release -r "$RID" --self-contained true -p:UseAppHost=true -o "$WORK/publish"
cp -R "$WORK/publish/." "$APP/Contents/MacOS/"
cp "$PLIST_SRC" "$APP/Contents/Info.plist"
cp "$PROFILE" "$APP/Contents/embedded.provisionprofile"

# createdump ist ein eigenes Hilfsprogramm von .NET; in der Sandbox unnötig und es würde eigene Berechtigungen brauchen
rm -f "$APP/Contents/MacOS/createdump"

# Symbol aus dem PNG erzeugen (wie build-app.sh)
PNG=$(ls Assets/*.png 2>/dev/null | head -1 || true)
if [ -n "$PNG" ]; then
    SET="$WORK/netstuff.iconset"
    mkdir -p "$SET"
    for s in 16 32 128 256 512; do
        sips -z $s $s "$PNG" --out "$SET/icon_${s}x${s}.png" >/dev/null
        sips -z $((s*2)) $((s*2)) "$PNG" --out "$SET/icon_${s}x${s}@2x.png" >/dev/null
    done
    iconutil -c icns "$SET" -o "$APP/Contents/Resources/netstuff.icns"
else
    echo "WARNUNG: kein Symbol in Assets/*.png gefunden – der App Store verlangt ein App-Symbol." >&2
fi

# --- Signieren: erst alle eingebetteten Bibliotheken, dann die App ---------------------------
xattr -cr "$APP"
MAIN_EXE="$APP/Contents/MacOS/$(/usr/libexec/PlistBuddy -c "Print :CFBundleExecutable" "$PLIST_SRC")"
# Alles in Contents/MacOS zählt für macOS als Programmcode – auch die .NET-DLLs – und muss einzeln signiert sein.
# Außerhalb davon nur Mach-O-Dateien signieren; der Rest wird als Ressource versiegelt.
find "$APP/Contents" -type f ! -path "$MAIN_EXE" -print0 | while IFS= read -r -d '' f; do
    if [[ "$f" == "$APP/Contents/MacOS/"* ]] || file -b "$f" | grep -q "Mach-O"; then
        codesign --force --sign "$APP_SIGN_ID" "$f" 2>&1 | grep -v "replacing existing signature" || true
    fi
done
codesign --force --sign "$APP_SIGN_ID" --entitlements "$ENTITLEMENTS" "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

# --- Installer-Paket -------------------------------------------------------------------------
PKG="$OUT/Net-Stuff-$VERSION-$BUILD.pkg"
productbuild --component "$APP" /Applications --sign "$INSTALLER_SIGN_ID" "$PKG"
pkgutil --check-signature "$PKG" >/dev/null

echo
echo "Fertig: $PKG"
echo "Hochladen: App „Transporter“ öffnen, Datei hineinziehen, „Deliver“ klicken."
