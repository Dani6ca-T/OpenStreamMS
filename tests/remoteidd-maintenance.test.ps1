param([Parameter(Mandatory)][string]$FixtureDirectory)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath($FixtureDirectory)
New-Item -ItemType Directory -Path $taskRoot -Force|Out-Null
# No executable is launched. Windows/WMI and process capabilities are test adapters.
New-Item -ItemType File -Path (Join-Path $taskRoot 'OpenStreamMS.exe') -Force|Out-Null
[IO.File]::WriteAllText((Join-Path $taskRoot 'operation-mode.json'),'{}')
$script:fixtureServicePath='"'+(Join-Path $taskRoot 'OpenStreamMS.exe')+'" --run'
function Get-CimInstance { param($ClassName,$Filter,$ErrorAction) [pscustomobject]@{PathName=$script:fixtureServicePath} }
function Start-Process { param($FilePath,$ArgumentList,$WindowStyle,[switch]$Wait,[switch]$PassThru,$ErrorAction) if($ArgumentList -ne '--check-maintenance'){throw 'Unexpected process capability'};[pscustomobject]@{ExitCode=$script:checkerExit} }
$helper=Join-Path $PSScriptRoot '../Native/RemoteIdd/Provider/termservice.ps1'
foreach($exitCode in @(3,1,9)) {
 $script:checkerExit=$exitCode;$blocked=$false
 try {. $helper} catch {$blocked=$true}
 if(!$blocked){throw "Safe/unreadable policy accepted (exit=$exitCode)"}
 Write-Output "PASS RemoteIdd blocks checker exit $exitCode before helper actions"
}
$script:checkerExit=0
. $helper
Write-Output 'PASS Normal RemoteIdd policy gate permits the existing script path (no helper invoked)'
foreach($name in @('install.ps1','uninstall.ps1')) {
 $text=Get-Content -LiteralPath (Join-Path $PSScriptRoot "../Native/RemoteIdd/Provider/$name") -Raw
 if($text -notmatch 'try \{ Assert-OpenStreamNormalMode \} catch \{ Write-Error \$_; exit 1 \}') {throw 'Missing terminating entry guard'}
}
Write-Output 'PASS both privileged scripts have terminating entry guards'
