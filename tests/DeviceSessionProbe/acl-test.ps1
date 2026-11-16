param([Parameter(Mandatory)][string]$Dev,   # instance id del dispositivo USB de prueba (no un teclado ni un ratón)
       [uint32]$Seat = 2, [uint32]$Console = 1,
      [Parameter(Mandatory)][string]$Src)   # carpeta con probe\DeviceSessionProbe.exe (dotnet build -o <Src>\probe)
# Corre como SYSTEM. Prueba el descriptor de seguridad del dispositivo (SPDRP_SECURITY_SDS) como aislamiento.
$ErrorActionPreference = 'Continue'
$W = 'C:\ProgramData\osms-sid-test'
$O = Join-Path $W 'out3'
New-Item -ItemType Directory -Force $O | Out-Null
Get-ChildItem $O -File | Remove-Item -Force
Copy-Item -Recurse -Force (Join-Path $Src 'probe') $W
icacls $W /grant '*S-1-5-32-545:(OI)(CI)M' /T /Q | Out-Null
$P = Join-Path $W 'probe\DeviceSessionProbe.exe'
$Paths = Join-Path $W 'paths.txt'

function Probe([string]$tag) {
    & $P probe $Dev --paths $Paths --out "$O\$tag-s0-system.txt"
    & $P spawn user $Console "$O\$tag-s$Console-user.txt" probe $Dev --paths $Paths
    & $P spawn user $Seat "$O\$tag-s$Seat-user.txt" probe $Dev --paths $Paths
}

try {
    $seatSid = (& $P usersid $Seat).Trim()
    $consoleSid = (& $P usersid $Console).Trim()
    "puesto=$seatSid consola=$consoleSid" | Set-Content "$O\sids.txt"

    # 1. Solo SYSTEM y el usuario del puesto; se reinicia para que el nuevo objeto de dispositivo la lleve.
    & $P acl $Dev "D:P(A;;GA;;;SY)(A;;GA;;;$seatSid)" --tree --restart --out "$O\1-acl-seat.txt"
    Start-Sleep -Seconds 3
    & $P acl $Dev get --tree --out "$O\1b-acl-get.txt"
    Probe '1-acl-seat'

    # 2. Mover a la consola: cambiar la ACL y reiniciar.
    & $P acl $Dev "D:P(A;;GA;;;SY)(A;;GA;;;$consoleSid)" --tree --restart --out "$O\2-acl-console.txt"
    Start-Sleep -Seconds 3
    Probe '2-acl-console'

    # 3. Cambiar la ACL SIN reiniciar: ¿se aplica a aperturas nuevas?
    & $P acl $Dev "D:P(A;;GA;;;SY)(A;;GA;;;$seatSid)" --tree --out "$O\3-acl-norestart.txt"
    Probe '3-acl-norestart'
}
finally {
    & $P acl $Dev clear --tree --restart --out "$O\9-clear.txt"
    Start-Sleep -Seconds 3
    Probe '9-clear'
    'fin' | Set-Content "$O\done.txt"
}
