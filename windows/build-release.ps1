# Builds a self-contained Windows package (no .NET install needed on the server) into dist/.
# Run from the repo root on a machine with the .NET 10 SDK.
$ErrorActionPreference = 'Stop'
$out = 'dist/TerraTeklaServer'
Remove-Item dist -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src/TerraTeklaServer -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
Copy-Item windows/install-service.ps1 $out
Compress-Archive -Path "$out/*" -DestinationPath dist/TerraTeklaServer-win-x64.zip
Write-Host "Built dist/TerraTeklaServer-win-x64.zip"
