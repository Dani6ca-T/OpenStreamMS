# uninstall.ps1 — quita OsmsPad: el dispositivo ROOT\OSMSPAD, el paquete del Driver Store y
# todos los certificados de prueba de OsmsPad (de cualquier compilación) de Root y TrustedPublisher.
param([switch] $KeepLog)
$ErrorActionPreference = 'Stop'
$log = Join-Path $env:ProgramData 'OpenStreamMS\osmspad-setup.log'
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
function Log($text) { "$(Get-Date -Format s) $text" | Tee-Object -FilePath $log -Append | Write-Host }

$setup = Join-Path $PSScriptRoot 'OsmsPadSetup.exe'
if (Test-Path $setup) {
    & $setup remove 2>&1 | ForEach-Object { Log $_ }
}
# Paquetes que se hubieran quedado sin dispositivo.
$drivers = Get-WindowsDriver -Online -ErrorAction SilentlyContinue | Where-Object { $_.OriginalFileName -like '*\osmspad.inf' }
foreach ($d in $drivers) {
    pnputil.exe /delete-driver $d.Driver /uninstall /force | Out-Null
    Log "Quitado el paquete $($d.Driver)."
}
foreach ($store in 'Root', 'TrustedPublisher') {
    Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object Subject -eq 'CN=OpenStreamMS OsmsPad Test' | ForEach-Object {
        Remove-Item $_.PSPath
        Log "Quitado el certificado $($_.Thumbprint) de $store."
    }
}
