# install.ps1 — instala OsmsPad desde el paquete de package.ps1 (como administrador).
#   1) quita una instalación anterior (dispositivo, paquete y su certificado);
#   2) importa OsmsPad.cer en Root y TrustedPublisher del equipo;
#   3) crea ROOT\OSMSPAD y le instala el driver (OsmsPadSetup.exe).
# Log en %ProgramData%\OpenStreamMS\osmspad-setup.log. Se deshace con uninstall.ps1.
$ErrorActionPreference = 'Stop'
$pkg = $PSScriptRoot
$log = Join-Path $env:ProgramData 'OpenStreamMS\osmspad-setup.log'
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
function Log($text) { "$(Get-Date -Format s) $text" | Tee-Object -FilePath $log -Append | Write-Host }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Hace falta ejecutarlo como administrador.' }
if ([Environment]::OSVersion.Version.Build -lt 19041) { throw 'OsmsPad necesita Windows 10 2004 (19041) o posterior.' }

& (Join-Path $pkg 'uninstall.ps1') -KeepLog

$cer = Join-Path $pkg 'OsmsPad.cer'
foreach ($store in 'Root', 'TrustedPublisher') {
    Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
}
Log "Certificado de OsmsPad importado en Root y TrustedPublisher."

$output = & (Join-Path $pkg 'OsmsPadSetup.exe') install (Join-Path $pkg 'driver\OsmsPad.inf') 2>&1
$code = $LASTEXITCODE
$output | ForEach-Object { Log $_ }
if ($code -ne 0 -and $code -ne 3010) { throw "OsmsPadSetup install falló ($code)." }
Log "OsmsPad instalado$(if ($code -eq 3010) { ' (hace falta reiniciar)' })."
exit $code
