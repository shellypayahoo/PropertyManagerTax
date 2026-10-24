#!/usr/bin/env bash
# Builds a Windows package that runs without installing .NET:
#   dist/PropertyManager-Windows/
#     Start Property Manager.cmd, README.txt
#     app/   (the app, .NET included, with the new-owner appsettings.json)
# and zips it to dist/PropertyManager-Windows.zip. Run from the repository root on a Mac, Windows, or Linux.
set -euo pipefail
cd "$(dirname "$0")/.."

out=dist/PropertyManager-Windows
rm -rf "$out" "$out.zip"
dotnet publish src/PropertyManager.Web -c Release -r win-x64 --self-contained true -o "$out/app" -p:GenerateDocumentationFile=false
cp packaging/appsettings.json "$out/app/appsettings.json"
cp packaging/appsettings.Local.example.json "$out/app/appsettings.Local.example.json"
rm -f "$out/app/appsettings.Development.json" "$out/app/appsettings.Local.json"
cp "packaging/windows/Start Property Manager.cmd" packaging/windows/README.txt "$out/"
(cd dist && zip -qr PropertyManager-Windows.zip PropertyManager-Windows)
echo "Built $out.zip"
