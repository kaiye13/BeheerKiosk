# Installeert ShipRipperServer.exe als Windows-service die automatisch start met de pc.
# Uitvoeren als administrator, vanuit de map waar ShipRipperServer.exe staat:
#   Rechtsklik PowerShell → "Als administrator uitvoeren" → .\install-server.ps1

$ErrorActionPreference = 'Stop'
$name = 'ShipRipperBoekhouding'
$exe = Join-Path $PSScriptRoot 'ShipRipperServer.exe'
$port = 5080

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    throw 'Start PowerShell als administrator.'
}
if (-not (Test-Path $exe)) { throw "ShipRipperServer.exe niet gevonden in $PSScriptRoot" }

if (Get-Service $name -ErrorAction SilentlyContinue) {
    Write-Host 'Service bestaat al: stoppen en bijwerken...'
    Stop-Service $name -ErrorAction SilentlyContinue
    sc.exe delete $name | Out-Null
    Start-Sleep 2
}

New-Service -Name $name -BinaryPathName "`"$exe`"" -DisplayName 'Ship & Ripper Boekhouding (server)' `
    -Description 'API + database voor de Ship & Ripper boekhouding' -StartupType Automatic | Out-Null
# Bij een crash na 1 minuut automatisch herstarten.
sc.exe failure $name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

# Andere pc's in het lokale netwerk toelaten (enkel privé-netwerken).
if (-not (Get-NetFirewallRule -DisplayName "$name ($port)" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName "$name ($port)" -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow -Profile Private | Out-Null
}

Start-Service $name
$ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.PrefixOrigin -in 'Dhcp', 'Manual' -and $_.IPAddress -notlike '169.*' } | Select-Object -First 1).IPAddress
Write-Host ''
Write-Host "Server draait. Vul op de andere pc's in bij Instellingen: http://$($ip):$port" -ForegroundColor Green
Write-Host "Database en geüploade facturen: $env:ProgramData\ShipRipperBoekhouding  (neem hier dagelijks een back-up van!)"
