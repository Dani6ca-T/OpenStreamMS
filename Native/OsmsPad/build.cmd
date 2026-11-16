@echo off
:: Compila OsmsPad sin instalar el WDK: usa el mismo paquete NuGet de Microsoft.Windows.WDK.x64
:: que RemoteIdd (Native\RemoteIdd\build.cmd lo descarga la primera vez).
:: Uso: build.cmd [carpeta de salida]
::   1) compila y ejecuta test\XboxSeriesTest.exe (si falla, para)
::   2) compila OsmsPad.dll (UMDF 2.15 + VHF) y test\OsmsPadTest.exe
::   3) sella y valida OsmsPad.inf
:: No firma ni instala nada.
setlocal
set OUT=%~1
if "%OUT%"=="" set OUT=%~dp0bin
if not exist "%OUT%" mkdir "%OUT%"

set WDKVER=10.0.26100.6584
set W=%LOCALAPPDATA%\OpenStreamMS\wdk-nuget\%WDKVER%\c
set WINC=%W%\Include\10.0.26100.0
set UMDF=2.15
set WLIB=%W%\Lib\10.0.26100.0\um\x64

set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
set VSDIR=
for /f "usebackq delims=" %%i in (`call "%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set VSDIR=%%i
if not defined VSDIR (
    echo ERROR: no se encontro MSVC ^(Build Tools con C++^).
    exit /b 1
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1

set OBJ=%TEMP%\osms-osmspad-obj
if not exist "%OBJ%" mkdir "%OBJ%"

:: ---- 1) prueba del perfil (sin WDK) ----
cl /nologo /utf-8 /O2 /W4 /WX /EHsc /std:c++17 /Fo"%OBJ%\\" ^
  "%~dp0test\XboxSeriesTest.cpp" "%~dp0XboxSeries.cpp" /Fe"%OBJ%\XboxSeriesTest.exe" || exit /b 1
"%OBJ%\XboxSeriesTest.exe" || exit /b 1

:: ---- 2) herramienta de prueba con el driver instalado ----
cl /nologo /utf-8 /O2 /W4 /WX /EHsc /std:c++17 /DUNICODE /D_UNICODE /Fo"%OBJ%\\" ^
  "%~dp0test\OsmsPadTest.cpp" /Fe"%OUT%\OsmsPadTest.exe" /link cfgmgr32.lib xinput.lib || exit /b 1
cl /nologo /utf-8 /O2 /W4 /WX /EHsc /std:c++17 /MT /DUNICODE /D_UNICODE /Fo"%OBJ%\\" ^
  "%~dp0setup\OsmsPadSetup.cpp" /Fe"%OUT%\OsmsPadSetup.exe" /link setupapi.lib newdev.lib advapi32.lib || exit /b 1

:: ---- 3) driver ----
if not exist "%W%\Include\wdf\umdf\%UMDF%\wdf.h" (
    echo ERROR: falta el WDK NuGet en %W%: ejecuta antes Native\RemoteIdd\build.cmd.
    exit /b 1
)
:: C4324 sale de las cabeceras de WDF (wdfrequest.h), no del código del driver.
cl /nologo /utf-8 /c /O2 /W4 /WX /wd4324 /EHsc /std:c++17 /MT /GS /guard:cf /Zi /Fo"%OBJ%\\" /Fd"%OBJ%\\" ^
  /DUNICODE /D_UNICODE /D_WINDLL /DWIN32_LEAN_AND_MEAN ^
  /DUMDF_VERSION_MAJOR=2 /DUMDF_VERSION_MINOR=15 /DUMDF_USING_NTSTATUS ^
  /I"%W%\Include\wdf\umdf\%UMDF%" /I"%WINC%\um" /I"%WINC%\shared" ^
  "%~dp0Driver.cpp" "%~dp0XboxSeries.cpp" || exit /b 1
link /nologo /DLL /OUT:"%OUT%\OsmsPad.dll" /PDB:"%OUT%\OsmsPad.pdb" /DEBUG /INCREMENTAL:NO /GUARD:CF ^
  /NODEFAULTLIB:kernel32.lib "%OBJ%\Driver.obj" "%OBJ%\XboxSeries.obj" ^
  "%W%\Lib\wdf\umdf\x64\%UMDF%\WdfDriverStubUm.lib" "%WLIB%\VhfUm.lib" OneCoreUAP.lib ntdll.lib || exit /b 1
del /q "%OUT%\OsmsPad.lib" "%OUT%\OsmsPad.exp" 2>nul

:: ---- 4) INF sellado y validado ----
copy /y "%~dp0OsmsPad.inf" "%OUT%\OsmsPad.inf" >nul
:: Fecha UTC (Inf2Cat compara con UTC; ver Native\RemoteIdd\build.cmd).
for /f %%d in ('powershell -NoProfile -Command "(Get-Date).ToUniversalTime().ToString('MM/dd/yyyy', [Globalization.CultureInfo]::InvariantCulture)"') do set DRIVER_DATE=%%d
"%W%\bin\10.0.26100.0\x64\stampinf.exe" -f "%OUT%\OsmsPad.inf" -a amd64 -u %UMDF%.0 -d %DRIVER_DATE% -v 0.1.0.0 >nul || exit /b 1
"%W%\tools\10.0.26100.0\x64\infverif.exe" /w "%OUT%\OsmsPad.inf" || exit /b 1

echo OsmsPad compilado en %OUT% (sin firmar, sin instalar).
exit /b 0
