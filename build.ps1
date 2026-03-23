# ProviderStuff 10.11 build script (PowerShell)
# Requires: Docker (or dotnet SDK 8.0 installed locally)

$ErrorActionPreference = "Stop"
$OutDir = ".\dist"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Write-Host ">> Building with local dotnet SDK..."
    Push-Location Jellyfin.Plugin.ProviderStuff
    dotnet restore
    dotnet publish -c Release -o "..\$OutDir"
    Pop-Location
} elseif (Get-Command docker -ErrorAction SilentlyContinue) {
    Write-Host ">> dotnet not found, building with Docker..."
    docker build --target export --output $OutDir .
} else {
    Write-Error "Neither dotnet SDK nor Docker found. Install one of them first."
    exit 1
}

Write-Host ""
Write-Host "Done. DLL is in $OutDir\"
Write-Host ""
Write-Host "Install:"
Write-Host "  1. Stop jellyfin container"
Write-Host "  2. Create: /mnt/cache_speedy/appdata_sp/jellyfin/config/plugins/ProviderStuff_1.3.0.0/"
Write-Host "  3. Copy $OutDir\Jellyfin.Plugin.ProviderStuff.dll into that folder"
Write-Host "  4. Start jellyfin container"
