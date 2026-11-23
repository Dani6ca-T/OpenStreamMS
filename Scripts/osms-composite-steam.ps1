# OpenStreamMS: app "Steam Big Picture" de una sesión compuesta.
#
# El Sunshine de la compuesta corre en la sesión de Windows de un solo miembro (el del teclado), así
# que lanzar Steam desde ahí lo abriría solo en esa sesión. Este script pide al servicio que lo abra
# (o lo cierre) en las sesiones de todos los huecos. El servicio solo lo acepta sin credenciales si
# la petición sale de la sesión de Windows de esa compuesta.
param(
    [Parameter(Mandatory)] [guid] $Composite,
    [Parameter(Mandatory)] [ValidateSet('open', 'close')] [string] $Action
)

$ErrorActionPreference = 'Stop'

$port = 5000
$config = Join-Path $PSScriptRoot '..\service.config.json'
if (Test-Path $config) {
    $p = (Get-Content $config -Raw | ConvertFrom-Json).ApiPort
    if ($p) { $port = $p }
}

Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$port/api/composites/$Composite/steam/$Action" | Out-Null
