# Bouwt de twee exe-bestanden in de map dist\
#   dist\Server\ShipRipperServer.exe       → op de pc die altijd aan staat
#   dist\Client\ShipRipperBoekhouding.exe  → op elke pc die de boekhouding gebruikt
# Beide zijn "self-contained": er hoeft geen .NET geïnstalleerd te zijn.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained',
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none')

dotnet test tests/Boekhouding.Tests

# Bestaande instellingen van de server (met sleutels) nooit overschrijven bij een nieuwe build.
$serverSettings = 'dist/Server/appsettings.json'
$kept = if (Test-Path $serverSettings) { [IO.File]::ReadAllBytes((Resolve-Path $serverSettings)) } else { $null }
dotnet publish src/Boekhouding.Api @common -o dist/Server
if ($kept) {
    [IO.File]::WriteAllBytes((Join-Path $PSScriptRoot $serverSettings), $kept)
    Write-Host "dist/Server/appsettings.json behouden (niet overschreven)." -ForegroundColor Yellow
}
dotnet publish src/Boekhouding.Client @common -o dist/Client
# De laatste versie van de boekhoud-app ook in de hoofdmap (die gaat mee in git, als download).
Copy-Item dist/Client/ShipRipperBoekhouding.exe . -Force
Copy-Item install-server.ps1, uninstall-server.ps1 dist/Server -Force

Write-Host ""
Write-Host "Klaar:" -ForegroundColor Green
Get-ChildItem dist/Server, dist/Client -File | Select-Object FullName, @{n='MB'; e={[math]::Round($_.Length / 1MB, 1)}} | Format-Table -AutoSize
