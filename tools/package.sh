#!/bin/sh
# Builds and packages HP Control on Linux (Mono reference assemblies + .NET SDK), like the release workflow does on
# Windows. Usage: tools/package.sh  ->  HPControl-<version>.zip and its SHA-256.
set -e
cd "$(dirname "$0")/.."
ver=$(sed -n 's/.*Version = "\([0-9.]*\)".*/\1/p' src/Common/Protocol.cs | head -1)
dotnet build src/Service/Service.csproj -c Release -o out/bin
dotnet build src/App/App.csproj -c Release -o out/bin
rm -rf stage && mkdir -p stage/HPControl/bin
for f in install.cmd install.ps1 uninstall.cmd uninstall.ps1 README.md; do sed 's/\r$//; s/$/\r/' "installer/$f" > "stage/HPControl/$f"; done
cp out/bin/HPControl.exe out/bin/HPControl.exe.config out/bin/HPControl.Service.exe out/bin/HPControl.Service.exe.config stage/HPControl/bin/
rm -f "HPControl-$ver.zip"
(cd stage && zip -r -X "../HPControl-$ver.zip" HPControl >/dev/null)
sha256sum "HPControl-$ver.zip"
