param([Parameter(Mandatory)][string]$Dev,   # instance id del dispositivo USB de prueba (no un teclado ni un ratón)
       [uint32]$Seat = 2, [uint32]$Console = 1,
      [Parameter(Mandatory)][string]$Src)   # carpeta con probe\DeviceSessionProbe.exe (dotnet build -o <Src>\probe)
# Corre como SYSTEM (tarea programada). Prueba DEVPKEY_Device_SessionId sobre $Dev y su subárbol.
$ErrorActionPreference = 'Continue'
$W = 'C:\ProgramData\osms-sid-test'
$O = Join-Path $W 'out'
New-Item -ItemType Directory -Force $O | Out-Null
Get-ChildItem $O -File | Remove-Item -Force
Copy-Item -Recurse -Force (Join-Path $Src 'probe') $W
icacls $W /grant '*S-1-5-32-545:(OI)(CI)M' /T /Q | Out-Null    # Usuarios: leer el exe y escribir resultados
$P = Join-Path $W 'probe\DeviceSessionProbe.exe'
$Paths = Join-Path $W 'paths.txt'

function Probe([string]$tag) {
    & $P probe $Dev --paths $Paths --out "$O\$tag-s0-system.txt"
    & $P spawn user $Console "$O\$tag-s$Console-user.txt" probe $Dev --paths $Paths
    & $P spawn user $Seat "$O\$tag-s$Seat-user.txt" probe $Dev --paths $Paths
}

try {
    & $P paths $Dev $Paths --out "$O\paths-log.txt"
    Probe '0-base'

    # 1. Fijar con el dispositivo en marcha (sin reiniciar)
    & $P set $Dev $Seat --tree --out "$O\1-set.txt"
    Probe '1-set'

    # 2. Deshabilitar, fijar, habilitar: ¿sobrevive la propiedad al arranque?
    & $P clear $Dev --tree --out "$O\2a-clear.txt"
    & $P disable $Dev --out "$O\2b-disable.txt"
    & $P set $Dev $Seat --tree --out "$O\2c-set-disabled.txt"
    & $P enable $Dev --out "$O\2d-enable.txt"
    Start-Sleep -Seconds 3
    & $P get $Dev --out "$O\2e-get.txt"
    Probe '2-disable-set-enable'

    # 3. Reiniciar y volver a fijar justo después (lo que haría el servicio al ver arrancar el dispositivo)
    & $P restart $Dev --out "$O\3a-restart.txt"
    Start-Sleep -Seconds 1   # que existan ya los hijos HID
    & $P set $Dev $Seat --tree --out "$O\3b-set-after-start.txt"
    Start-Sleep -Seconds 2
    Probe '3-set-after-start'
}
finally {
    & $P clear $Dev --tree --restart --out "$O\9-clear.txt"
    Start-Sleep -Seconds 3
    Probe '9-clear'
    'fin' | Set-Content "$O\done.txt"
}
