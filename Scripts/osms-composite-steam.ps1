# OpenStreamMS: app "Steam Big Picture" de una sesión compuesta.
#
# El Sunshine de la compuesta corre en la sesión de Windows de un solo miembro (el del teclado), así
# que lanzar Steam desde ahí lo abriría solo en esa sesión. Este script pide al servicio que lo abra
# (o lo cierre) en las sesiones de todos los huecos. El servicio solo lo acepta sin credenciales si
# la petición sale de la sesión de Windows de esa compuesta.
#
# Sale siempre con 0: Sunshine trata un código distinto como que la app terminó y corta el stream.
# Los errores quedan en %TEMP%\osms-composite-steam.log.
param(
    [Parameter(Mandatory)] [guid] $Composite,
    [Parameter(Mandatory)] [ValidateSet('open', 'close')] [string] $Action
)

try {
    # service.config.json no se puede leer sin ser administrador (lleva secretos): el servicio
    # publica su puerto en el registro, como para el icono de bandeja.
    $port = 5000
    $p = (Get-ItemProperty 'HKLM:\SOFTWARE\OpenStreamMS' -Name ApiPort -ErrorAction SilentlyContinue).ApiPort
    if ($p -gt 0) { $port = $p }

    Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$port/api/composites/$Composite/steam/$Action" `
                      -TimeoutSec 30 | Out-Null
}
catch {
    try {
        "$(Get-Date -Format s) $Action $Composite : $($_.Exception.Message)" |
            Add-Content -Path (Join-Path $env:TEMP 'osms-composite-steam.log')
    } catch { }
}
exit 0
