#!/usr/bin/env bash
# ProviderStuff 10.11 build script
# Requires: Docker (or dotnet SDK 8.0 installed locally)
# Output: ./dist/Jellyfin.Plugin.ProviderStuff.dll

set -e
OUTDIR="./dist"
mkdir -p "$OUTDIR"

if command -v dotnet &>/dev/null; then
    echo ">> Building with local dotnet SDK..."
    cd Jellyfin.Plugin.ProviderStuff
    dotnet restore
    dotnet publish -c Release -o "../$OUTDIR"
    cd ..
    echo ">> Done. DLL is in $OUTDIR/"
elif command -v docker &>/dev/null; then
    echo ">> dotnet not found, building with Docker..."
    docker build --target export --output "$OUTDIR" .
    echo ">> Done. DLL is in $OUTDIR/"
else
    echo "ERROR: Neither dotnet SDK nor Docker found. Install one of them first."
    exit 1
fi

echo ""
echo "Install instructions:"
echo "  1. Stop jellyfin container"
echo "  2. Create folder: /mnt/cache_speedy/appdata_sp/jellyfin/config/plugins/ProviderStuff_1.3.0.0/"
echo "  3. Copy $OUTDIR/Jellyfin.Plugin.ProviderStuff.dll into that folder"
echo "  4. Start jellyfin container"
echo "  5. Run 'ProviderStuff: Apply provider tags' from Scheduled Tasks"
