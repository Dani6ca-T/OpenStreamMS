# OpenStreamMS: apaga o reinicia el PC desde una app de Moonlight (Reboot / Power Off).
#
# Lo lanza Sunshine dentro de la sesión de stream, con un usuario sin privilegios y con
# otros usuarios conectados: ahí shutdown.exe falla ("hay otras personas usando este
# equipo"). Por eso se pide al servicio de OpenStreamMS (SYSTEM), por la misma vía que el HUD:
# sin credenciales solo desde la sesión de Windows de una sesión en marcha, y solo si en
# Ajustes → HUD está permitido apagar y reiniciar el PC.
#
# Sale siempre con 0 (Sunshine corta el stream si una app termina con error). Los fallos quedan
# en %TEMP%\osms-power.log.
param(
    [Parameter(Mandatory)] [ValidateSet('restart', 'shutdown')] [string] $Action
)

try {
    # service.config.json no se puede leer sin ser administrador (lleva secretos): el servicio
    # publica su puerto en el registro, como para el icono de bandeja.
    $port = 5000
    $p = (Get-ItemProperty 'HKLM:\SOFTWARE\OpenStreamMS' -Name ApiPort -ErrorAction SilentlyContinue).ApiPort
    if ($p -gt 0) { $port = $p }

    Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$port/api/hud/power/$Action" -TimeoutSec 30 | Out-Null
}
catch {
    try {
        "$(Get-Date -Format s) $Action : $($_.Exception.Message)" |
            Add-Content -Path (Join-Path $env:TEMP 'osms-power.log')
    } catch { }
}
exit 0
