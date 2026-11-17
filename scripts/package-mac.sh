#!/usr/bin/env bash
# Builds the Mac app and disk image: dist/PropertyManager-<version>-<arm64|x64>.dmg, containing
# "Property Manager.app" and a shortcut to Applications. Run on a Mac (GitHub does it in installers.yml):
#   scripts/package-mac.sh arm64 1.2.3
# The app isn't signed by Apple, so the first time it's opened macOS asks for confirmation (see docs/install-mac.md).
set -euo pipefail
cd "$(dirname "$0")/.."
arch="${1:-arm64}"
version="${2:-0.0.0}"

work="dist/mac-$arch"
bundle="$work/Property Manager.app"
rm -rf "$work"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"

dotnet publish src/PropertyManager.Web -c Release -r "osx-$arch" --self-contained true -p:DesktopApp=true -p:Version="$version" \
    -o "$bundle/Contents/Resources/app"
cp packaging/appsettings.json "$bundle/Contents/Resources/app/appsettings.json"
rm -f "$bundle/Contents/Resources/app/appsettings.Development.json" "$bundle/Contents/Resources/app/appsettings.Local.json"
touch "$bundle/Contents/Resources/app/desktop-app"
cp packaging/mac/launcher.sh "$bundle/Contents/MacOS/Property Manager"
chmod +x "$bundle/Contents/MacOS/Property Manager"
sed "s/APP_VERSION/$version/g" packaging/mac/Info.plist > "$bundle/Contents/Info.plist"

# Apple Silicon only runs signed code; an ad-hoc signature (no Apple account) is enough for that.
codesign --force --deep --sign - "$bundle"

ln -s /Applications "$work/Applications"
hdiutil create -volname "Property Manager" -srcfolder "$work" -ov -format UDZO "dist/PropertyManager-$version-$arch.dmg"
echo "Built dist/PropertyManager-$version-$arch.dmg"
