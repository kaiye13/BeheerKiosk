# Verwijdert de Windows-service en de firewallregel. De database blijft staan in %ProgramData%\ShipRipperBoekhouding.
# Uitvoeren als administrator.

$name = 'ShipRipperBoekhouding'
Stop-Service $name -ErrorAction SilentlyContinue
sc.exe delete $name
Remove-NetFirewallRule -DisplayName "$name (5080)" -ErrorAction SilentlyContinue
Write-Host "Service verwijderd. Data staat nog in $env:ProgramData\ShipRipperBoekhouding"
