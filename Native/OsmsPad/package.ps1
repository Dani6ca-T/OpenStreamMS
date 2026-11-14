# package.ps1 — paquete de OsmsPad ya firmado, como Core\Installer\package-remoteidd.ps1.
#
# Firma de PRUEBA con un certificado autofirmado de un solo uso: se crea en cada compilación y
# su clave privada se borra en cuanto se ha firmado, así que nadie puede firmar nada más con él.
# Al paquete solo va la parte pública (OsmsPad.cer), que install.ps1 mete en Root y
# TrustedPublisher. Es un driver UMDF (sin código de kernel propio): carga sin testsigning.
#
# Uso: package.ps1 -Bin <salida de build.cmd> -OutDir <carpeta del paquete>
param(
    [Parameter(Mandatory)] [string] $Bin,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'
$wdk = Join-Path $env:LOCALAPPDATA 'OpenStreamMS\wdk-nuget\10.0.26100.6584\c'
$inf2cat = "$wdk\bin\10.0.26100.0\x86\Inf2Cat.exe"
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName

foreach ($f in 'OsmsPad.dll', 'OsmsPad.inf', 'OsmsPadSetup.exe', 'OsmsPadTest.exe') {
    if (-not (Test-Path (Join-Path $Bin $f))) { throw "Falta $f en $Bin (ejecuta Native\OsmsPad\build.cmd)." }
}
if (-not $signtool) { throw 'No se encuentra signtool.exe (hace falta el SDK de Windows).' }
if (-not (Test-Path $inf2cat)) { throw "No se encuentra $inf2cat (lo descarga Native\RemoteIdd\build.cmd)." }

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=OpenStreamMS OsmsPad Test' `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3)
function Sign($file) {
    & $signtool sign /fd sha256 /s My /sha1 $cert.Thumbprint $file | Out-Null
    if ($LASTEXITCODE) { throw "signtool falló al firmar $file." }
}
try {
    Remove-Item -Recurse -Force $OutDir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $drv = Join-Path $OutDir 'driver'
    New-Item -ItemType Directory $drv | Out-Null
    Copy-Item (Join-Path $Bin 'OsmsPad.dll'), (Join-Path $Bin 'OsmsPad.inf') $drv
    Copy-Item (Join-Path $Bin 'OsmsPadSetup.exe'), (Join-Path $Bin 'OsmsPadTest.exe') $OutDir
    Sign (Join-Path $drv 'OsmsPad.dll')
    Sign (Join-Path $OutDir 'OsmsPadSetup.exe')
    Sign (Join-Path $OutDir 'OsmsPadTest.exe')
    & $inf2cat /driver:$drv /os:10_X64 | Out-Null
    if ($LASTEXITCODE) { throw 'Inf2Cat falló (¿DriverVer con fecha futura?).' }
    Sign (Join-Path $drv 'OsmsPad.cat')
    Export-Certificate -Cert $cert -FilePath (Join-Path $OutDir 'OsmsPad.cer') | Out-Null
}
finally {
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey -ErrorAction SilentlyContinue
}
Copy-Item (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1') $OutDir
Write-Host "Paquete OsmsPad listo en $OutDir (certificado de un solo uso $($cert.Thumbprint), clave borrada)."
