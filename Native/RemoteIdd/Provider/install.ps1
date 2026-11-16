# install.ps1 — instala RemoteIdd + proveedor RDS para PRUEBAS (firma de test).
#
# ⚠️ Pensado para una VM o un equipo de pruebas, no para el PC de los puestos:
#   - exige testsigning activado (bcdedit /set testsigning on + reinicio; con arranque
#     seguro activo hay que desactivarlo antes). Este script NO lo activa.
#   - importa un certificado de test en Root y TrustedPublisher del equipo.
# Lo que toca, y uninstall.ps1 deshace con el fichero de estado:
#   1) certificado de test        2) driver en el Driver Store (pnputil)
#   3) OsmsRdsProvider.dll en Program Files + su clase COM (HKLM)
#   4) No toca TermService. En Windows cliente termsrv rechaza toda pila de protocolo que no
#      sea la de Microsoft (CRemoteConnectionManager::IsAllowNonRDPStack): sin algo que lo
#      permita, TermService no carga el proveedor y RDP-Tcp no escucha. OpenStreamMS no instala
#      nada para eso; si al arrancar ve RDP-Tcp sin escuchar, quita RemoteIdd de RDP-Tcp.
#      (Instalaciones anteriores cambiaban aquí ServiceDll; uninstall.ps1 lo sigue deshaciendo.)
#   5) RDP-Tcp: LoadableProtocol_Object pasa a este proveedor (el original se guarda en
#      OsmsInnerProtocol_Object y en el estado) + OsmsRefreshHz.
# Windows cliente (Pro) solo levanta el listener RDP-Tcp, así que el proveedor va ahí, como
# hace DuoStream. Solo las conexiones de puesto (cliente 127.0.0.2) reciben RemoteIdd; el
# resto de conexiones RDP siguen enteras con el protocolo y el RdpIdd de Microsoft.
#
# Uso (administrador):  .\install.ps1 -Bin <salida de build.cmd> [-RefreshHz 120] [-AllConnections] [-RestartTermService] [-SkipTestSigningCheck]
#                       .\install.ps1 -Package <paquete firmado> ...  (lo que usa el instalador)
# -Package: carpeta de Core\Installer\package-remoteidd.ps1, con el driver, su catálogo y el
#                  proveedor ya firmados y la parte pública del certificado (RemoteIdd.cer). No
#                  necesita el SDK ni el WDK: solo importa el certificado y registra lo firmado.
# -AllConnections: RemoteIdd también en las sesiones RDP que llegan por la red, no solo en los
#                  puestos locales. Por defecto, solo conexiones desde el propio equipo.
# -RefreshHz: Hz de la pantalla del puesto (RDP no los transmite). Por defecto, 60.
# -SkipTestSigningCheck: instala sin testsigning. RemoteIdd es UMDF (modo usuario) y ha cargado
#                  sin él con el certificado de test en Root y TrustedPublisher; útil con
#                  BitLocker, donde activar testsigning pide la clave de recuperación.
param(
    [string] $Bin,
    [string] $Package,
    [ValidateRange(24, 1000)] [int] $RefreshHz = 60,
    [switch] $AllConnections,
    [switch] $RestartTermService,
    [switch] $SkipTestSigningCheck
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'termservice.ps1')
if ([bool]$Bin -eq [bool]$Package) { throw 'Indica -Bin (desarrollo) o -Package (paquete firmado), uno de los dos.' }
# Desde el instalador no hay consola que mirar: lo que pase queda en un log.
if ($Package) { Start-Transcript -Path (Join-Path $env:ProgramData 'OpenStreamMS\remoteidd-setup.log') -Append | Out-Null }

$state = Join-Path $env:ProgramData 'OpenStreamMS\remoteidd-install.json'
$clsid = '{8B0F3C51-2D7A-4F6E-A1C9-5E4D2B7F0A36}'
$ws    = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations'
$wdk   = Join-Path $env:LOCALAPPDATA 'OpenStreamMS\wdk-nuget\10.0.26100.6584\c'
$sdk   = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64"

# Rol integrado y no el nombre del grupo: en Windows en español es "Administradores".
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Hay que ejecutarlo como administrador.'
}
if (Test-Path $state) { throw "Ya instalado (ver $state). Ejecuta antes uninstall.ps1." }
# Opciones de arranque en vigor (no depende del idioma, a diferencia de la salida de bcdedit).
if (-not $SkipTestSigningCheck -and
    (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control').SystemStartOptions -notmatch 'TESTSIGNING') {
    throw 'testsigning no está activo. Solo para pruebas: bcdedit /set testsigning on y reiniciar (en una VM).'
}
$src = if ($Package) { $Package } else { $Bin }
$needed = 'RemoteIdd.dll', 'RemoteIdd.inf', 'OsmsRdsProvider.dll'
if ($Package) { $needed += 'RemoteIdd.cat', 'RemoteIdd.cer' }
foreach ($f in $needed) {
    if (-not (Test-Path (Join-Path $src $f))) { throw "Falta $f en $src (ejecuta build.cmd o package-remoteidd.ps1)." }
}
$rdpTcp = "$ws\RDP-Tcp"
$original = (Get-ItemProperty $rdpTcp).LoadableProtocol_Object
if (-not $original) { throw 'RDP-Tcp no tiene LoadableProtocol_Object: no es el RDP de Windows esperado.' }
if ($original -eq $clsid) { throw 'RDP-Tcp ya apunta a este proveedor (¿instalación anterior a medias?).' }

$done = [ordered]@{}

# signtool: parar si falla (si no, el error saldría después como "driver sin firmar").
function Sign($file) {
    & "$sdk\signtool.exe" sign /fd sha256 /sm /s My /sha1 $cert.Thumbprint $file | Out-Null
    if ($LASTEXITCODE) { throw "signtool falló al firmar $file." }
}
# regsvr32 es una aplicación gráfica: PowerShell no la espera ni recoge su código.
function RegSvr($arguments) {
    $p = Start-Process regsvr32.exe -ArgumentList $arguments -Wait -PassThru
    if ($p.ExitCode) { throw "regsvr32 $arguments falló ($($p.ExitCode))." }
}
function Save { New-Item -ItemType Directory -Force (Split-Path $state) | Out-Null; $done | ConvertTo-Json | Set-Content -Encoding utf8 $state }

# 1) certificado de test: con -Package, el público que viene firmado en el paquete; si no, uno
#    nuevo de este equipo.
if ($Package) {
    $cer = Join-Path $Package 'RemoteIdd.cer'
    $cert = Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
} else {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=OpenStreamMS RemoteIdd Test' `
            -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(1)
    $cer = Join-Path $env:TEMP 'osms-remoteidd-test.cer'
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
    Remove-Item $cer
}
$done.Thumbprint = $cert.Thumbprint; Save

# 2) paquete del driver firmado (con -Package ya viene con su catálogo)
if ($Package) {
    $pkg = $Package
} else {
    $pkg = Join-Path $env:TEMP 'osms-remoteidd-pkg'
    Remove-Item -Recurse -Force $pkg -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $pkg | Out-Null
    Copy-Item (Join-Path $Bin 'RemoteIdd.dll'), (Join-Path $Bin 'RemoteIdd.inf') $pkg
    Sign "$pkg\RemoteIdd.dll"
    & "$wdk\bin\10.0.26100.0\x86\Inf2Cat.exe" /driver:$pkg /os:10_X64 | Out-Null
    if ($LASTEXITCODE) { throw 'Inf2Cat falló.' }
    Sign "$pkg\RemoteIdd.cat"
}
$out = pnputil /add-driver "$pkg\RemoteIdd.inf" /install
# 3010 = requiere reinicio; 259 = añadido al Driver Store sin dispositivo aún (lo crea
# Terminal Services al abrir la sesión del puesto). Ambos son éxito aquí.
if ($LASTEXITCODE -and $LASTEXITCODE -notin 3010, 259) { throw "pnputil falló ($LASTEXITCODE):`n$out" }
$done.DriverInf = ([regex]::Match(($out -join "`n"), 'oem\d+\.inf')).Value; Save

# 3) proveedor COM
$dir = Join-Path $env:ProgramFiles 'OpenStreamMS\RemoteIdd'
New-Item -ItemType Directory -Force $dir | Out-Null
# Sin reiniciar TermService (el instalador pide reiniciar el equipo) la DLL anterior puede seguir cargada.
Copy-InUse (Join-Path $src 'OsmsRdsProvider.dll') (Join-Path $dir 'OsmsRdsProvider.dll')
if (-not $Package) { Sign "$dir\OsmsRdsProvider.dll" }   # en el paquete ya va firmada
RegSvr "/s `"$dir\OsmsRdsProvider.dll`""
$done.ProviderDir = $dir; Save

# Log del proveedor (lo escribe TermService, que corre como NETWORK SERVICE; *S-1-5-20).
$log = Join-Path $env:ProgramData 'OpenStreamMS\remoteidd-provider.log'
New-Item -ItemType File -Force $log | Out-Null
icacls $log /grant '*S-1-5-20:(M)' /grant '*S-1-5-32-545:(R)' | Out-Null
$done.ProviderLog = $log; Save
# Log del driver (lo escribe el WUDFHost, que corre como LOCAL SERVICE; *S-1-5-19).
$dlog = Join-Path $env:ProgramData 'OpenStreamMS\remoteidd-driver.log'
New-Item -ItemType File -Force $dlog | Out-Null
icacls $dlog /grant '*S-1-5-19:(M)' /grant '*S-1-5-32-545:(R)' | Out-Null
$done.DriverLog = $dlog; Save

# 4) Solo un aviso (ver cabecera): OpenStreamMS no cambia ServiceDll ni instala nada para
#    varias sesiones.
$serviceDll = (Get-Item 'HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters').GetValue('ServiceDll', $null, 'DoNotExpandEnvironmentNames')
$isServer = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ProductOptions').ProductType -ne 'WinNT'
if (-not $isServer -and $serviceDll -match '\\System32\\termsrv\.dll$') {
    Write-Warning ('Windows cliente con el termsrv.dll de serie: no admitirá el proveedor de RemoteIdd y ' +
                   'RDP-Tcp no escuchará. OpenStreamMS lo quitará de RDP-Tcp al arrancar.')
}

# 5) RDP-Tcp: este proveedor delante del de Microsoft. El original primero en el estado,
#    para que uninstall.ps1 lo restaure aunque algo falle a continuación.
$done.OriginalProtocol = $original; Save
Set-ItemProperty $rdpTcp OsmsInnerProtocol_Object $original
Set-ItemProperty $rdpTcp OsmsRefreshHz $RefreshHz -Type DWord
Set-ItemProperty $rdpTcp OsmsRemoteIddAllConnections ([int][bool]$AllConnections) -Type DWord
Set-ItemProperty $rdpTcp LoadableProtocol_Object $clsid
$done.RdpTcpPatched = $true; Save

if ($RestartTermService) {
    Write-Warning 'Reiniciando TermService: se cierran TODAS las sesiones remotas abiertas.'
    Restart-TermService
} else {
    Write-Host 'Listo. El proveedor se carga al reiniciar TermService o el equipo.'
}
Write-Host 'Prueba: mstsc /v:127.0.0.2 con un usuario de puesto y test\verify-seat.ps1 -User <usuario>.'
