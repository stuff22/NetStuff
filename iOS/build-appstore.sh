#!/bin/bash
# Baut Net-Stuff für iPhone und iPad als signierte .ipa für den App Store (eine Datei für beide Geräte).
#
#   Aufruf im Repository-Ordner:  bash iOS/build-appstore.sh
#   Ergebnis:                     iOS/bin.nosync/appstore/Net-Stuff-iOS-<Version>-<Build>.ipa
#                                 → mit der App „Transporter“ hochladen
#
# Voraussetzungen (einmalig):
#   - .NET-Workload für iOS:  sudo dotnet workload install ios
#   - Zertifikat „Apple Distribution“ im Schlüsselbund (wie beim Mac-Build)
#   - Provisioning Profile vom Typ „App Store Connect“ (Plattform iOS) für at.itstuff.netstuff, abgelegt als
#     iOS/NetStuff_AppStore.mobileprovision (oder Pfad in PROFILE angeben)
#
# Optionale Umgebungsvariablen:
#   APP_SIGN_ID   Name des Zertifikats  (Standard: automatisch gesucht)
#   PROFILE       Pfad zum Provisioning Profile
#
# Vor jedem Upload muss die Build-Nummer (ApplicationVersion in iOS/NetStuff.iOS.csproj) höher sein als beim
# letzten Upload derselben Version; bei einer neuen Version (ApplicationDisplayVersion) darf sie wieder bei 1 beginnen.
set -euo pipefail
cd "$(dirname "$0")/.."

PROFILE=${PROFILE:-iOS/NetStuff_AppStore.mobileprovision}
CSPROJ=iOS/NetStuff.iOS.csproj
OUT=iOS/bin.nosync/appstore

fail() { echo "FEHLER: $*" >&2; exit 1; }

command -v dotnet >/dev/null || PATH="/usr/local/share/dotnet:$PATH"
command -v dotnet >/dev/null || fail "dotnet nicht gefunden – .NET SDK installieren (dot.net)."
dotnet workload list 2>/dev/null | grep -q "^ios " || fail "iOS-Workload fehlt:  sudo dotnet workload install ios"

# --- Zertifikat und Profil prüfen ------------------------------------------------------------
APP_SIGN_ID=${APP_SIGN_ID:-$(security find-identity -v -p codesigning | grep -E '"Apple Distribution|"iPhone Distribution' | head -1 | sed -E 's/.*"(.*)"/\1/' || true)}
[ -n "$APP_SIGN_ID" ] || fail "Kein Zertifikat „Apple Distribution“ im Schlüsselbund (Xcode → Settings → Accounts → Manage Certificates)."
[ -f "$PROFILE" ]     || fail "Provisioning Profile fehlt: $PROFILE (developer.apple.com → Profiles → „+“ → Distribution: App Store Connect → iOS)."

prop() { sed -nE "s#.*<$1>([^<]+)</$1>.*#\1#p" "$CSPROJ" | head -1; }
BUNDLE_ID=$(prop ApplicationId)
VERSION=$(prop ApplicationDisplayVersion)
BUILD=$(prop ApplicationVersion)

WORK=$(mktemp -d "${TMPDIR:-/tmp}/netstuff-ios.XXXXXX")
trap 'rm -rf "$WORK"' EXIT

security cms -D -i "$PROFILE" > "$WORK/profile.plist"
pb() { /usr/libexec/PlistBuddy -c "Print :$1" "$WORK/profile.plist" 2>/dev/null; }
PROFILE_NAME=$(pb Name)
PROFILE_UUID=$(pb UUID)
APP_IDENTIFIER=$(pb Entitlements:application-identifier)
TEAM_ID=$(pb Entitlements:com.apple.developer.team-identifier)
[ "$(pb Platform:0)" = "iOS" ]                || fail "Das Profil „$PROFILE_NAME“ ist kein iOS-Profil."
[ "$APP_IDENTIFIER" = "$TEAM_ID.$BUNDLE_ID" ] || fail "Profil gehört zu $APP_IDENTIFIER, die App hat aber die Bundle ID $BUNDLE_ID."
! pb ProvisionedDevices >/dev/null            || fail "„$PROFILE_NAME“ ist ein Entwicklungs-/Ad-hoc-Profil (mit Geräteliste). Gebraucht wird „App Store Connect“."
[ "$(pb Entitlements:get-task-allow)" != "true" ] || fail "„$PROFILE_NAME“ ist ein Entwicklungsprofil. Gebraucht wird „App Store Connect“."

# Das iOS-Werkzeug von .NET sucht Profile nur in den Ordnern, die auch Xcode verwendet
for d in "$HOME/Library/MobileDevice/Provisioning Profiles" "$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"; do
    mkdir -p "$d" && cp "$PROFILE" "$d/$PROFILE_UUID.mobileprovision"
done

echo "App:        $BUNDLE_ID $VERSION ($BUILD), iPhone + iPad"
echo "Signatur:   $APP_SIGN_ID"
echo "Profil:     $PROFILE_NAME"
echo

# --- Bauen, signieren, .ipa erzeugen ---------------------------------------------------------
rm -rf "$OUT"
mkdir -p "$OUT"
dotnet publish "$CSPROJ" -c Release -r ios-arm64 \
    -p:BuildIpa=true -p:IpaPackageDir="$WORK/ipa/" \
    -p:CodesignKey="$APP_SIGN_ID" -p:CodesignProvision="$PROFILE_NAME"

IPA_SRC=$(ls "$WORK"/ipa/*.ipa | head -1)
IPA="$OUT/Net-Stuff-iOS-$VERSION-$BUILD.ipa"
cp "$IPA_SRC" "$IPA"

# --- Prüfen ------------------------------------------------------------------------------------
mkdir "$WORK/check" && unzip -q "$IPA" -d "$WORK/check"
APP=$(ls -d "$WORK"/check/Payload/*.app)
codesign --verify --deep --strict "$APP" || fail "Signatur der App ist ungültig."
codesign -d --entitlements :- "$APP" 2>/dev/null > "$WORK/ent.plist"
[ "$(/usr/libexec/PlistBuddy -c 'Print :get-task-allow' "$WORK/ent.plist" 2>/dev/null)" != "true" ] \
    || fail "App wurde mit einem Entwicklungsprofil signiert (get-task-allow)."
[ -f "$APP/Assets.car" ] || fail "App-Symbol fehlt (iOS/Assets.xcassets)."

echo
echo "Fertig: $IPA"
echo "Hochladen: App „Transporter“ öffnen, Datei hineinziehen, „Deliver“ klicken."
