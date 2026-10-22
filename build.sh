#!/bin/sh
# Builds build/Morphy.app. Usage: ./build.sh [--selftest [corpus_dir] | --install | --dmg | --release]
#   --install  copies to /Applications and launches it
#   --dmg      also packs build/Morphy-<version>.dmg (app + Applications shortcut for drag-install)
#   --release  web release: Developer ID signature + timestamp, DMG, notarization, stapled ticket (docs/release.md)
# Every build uses the hardened runtime and Morphy.entitlements, so what is tested is what ships.
# Dev builds sign with the first "Apple Development" identity so Accessibility/Automation grants survive rebuilds
# (ad-hoc signatures change every build and macOS forgets the grant). Override with SIGN=<identity>.
set -e
cd "$(dirname "$0")"
VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" Info.plist)
ids=$(security find-identity -v -p codesigning)
if [ "$1" = "--release" ]; then
  SIGN=${SIGN:-$(echo "$ids" | awk '/Developer ID Application/ {print $2; exit}')}
  [ -n "$SIGN" ] || { echo "No 'Developer ID Application' certificate in the keychain (docs/release.md, step 1)." >&2; exit 1; }
  NOTARY_PROFILE=${NOTARY_PROFILE:-morphy-notary}
  STAMP=--timestamp
else
  SIGN=${SIGN:-$(echo "$ids" | awk '/Apple Development/ {print $2; exit}')}
  STAMP=--timestamp=none
fi

swift build -c release
APP=build/Morphy.app
rm -rf "$APP" && mkdir -p "$APP/Contents/MacOS"
cp .build/release/Morphy "$APP/Contents/MacOS/"
cp Info.plist "$APP/Contents/"
mkdir -p "$APP/Contents/Resources" && cp assets/AppIcon.icns "$APP/Contents/Resources/"  # regenerate: swift scripts/make_icon.swift
codesign --force --options runtime --entitlements Morphy.entitlements $STAMP --sign "${SIGN:--}" "$APP"

dmg() {
  STAGE=build/dmg
  rm -rf "$STAGE" && mkdir -p "$STAGE"
  cp -R "$APP" "$STAGE/"
  ln -s /Applications "$STAGE/Applications"
  OUT=build/Morphy-$VERSION.dmg
  hdiutil create -volname "Morphy $VERSION" -srcfolder "$STAGE" -fs HFS+ -format UDZO -ov "$OUT" >/dev/null
  rm -rf "$STAGE"
  codesign --force $STAMP --sign "${SIGN:--}" "$OUT"
}

case "$1" in
  --selftest) "$APP/Contents/MacOS/Morphy" "$@" ;;
  --install)
    osascript -e 'quit app id "app.morphy.Morphy"' 2>/dev/null || true
    rm -rf /Applications/Morphy.app && cp -R "$APP" /Applications/
    open /Applications/Morphy.app
    APP=/Applications/Morphy.app ;;
  --dmg) dmg; APP=$OUT ;;
  --release)
    "$APP/Contents/MacOS/Morphy" --selftest >/dev/null || { echo "Self-test failed; not releasing." >&2; exit 1; }
    dmg; APP=$OUT
    # Notarizing the DMG covers the app inside; the stapled ticket lets Gatekeeper pass it offline.
    xcrun notarytool submit "$OUT" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$OUT"
    xcrun stapler validate "$OUT"
    spctl --assess --type open --context context:primary-signature -v "$OUT"
    shasum -a 256 "$OUT" ;;
esac
echo "$APP"
