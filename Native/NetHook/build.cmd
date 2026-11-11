@echo off
:: Compila osms-nethook.dll y osms-netinject.exe en x64 y x86 (los juegos LAN suelen ser de 32 bits).
:: Uso: build.cmd <carpeta de salida>   ->  <salida>\x64\...  y  <salida>\x86\...
:: Con TEST=1 compila también nettest.exe en las dos.
setlocal
set OUT=%~1
if "%OUT%"=="" set OUT=%~dp0bin

set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
set VSDIR=
for /f "usebackq delims=" %%i in (`call "%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set VSDIR=%%i
if not defined VSDIR (
    echo ERROR: no se encontro MSVC ^(Build Tools con C++^).
    exit /b 1
)

call :arch x64 x64 || exit /b 1
call :arch x86 x64_x86 || exit /b 1
exit /b 0

:arch
setlocal
call "%VSDIR%\VC\Auxiliary\Build\vcvarsall.bat" %2 >nul || exit /b 1
set DEST=%OUT%\%1
if not exist "%DEST%" mkdir "%DEST%"
set OBJ=%TEMP%\osms-nethook-obj-%1
if not exist "%OBJ%" mkdir "%OBJ%"
set CFLAGS=/nologo /O2 /W4 /WX /MT /GS /DUNICODE /D_UNICODE /Fo"%OBJ%\\"
cl %CFLAGS% /LD "%~dp0nethook.c" /Fe"%DEST%\osms-nethook.dll" /link /DLL /DEF:"%~dp0nethook.def" || exit /b 1
cl %CFLAGS% "%~dp0injector.c" /Fe"%DEST%\osms-netinject.exe" /link /SUBSYSTEM:CONSOLE || exit /b 1
if /i "%TEST%"=="1" cl /nologo /O2 /W3 /MT /Fo"%OBJ%\\" "%~dp0nettest.c" /Fe"%DEST%\nettest.exe" || exit /b 1
del /q "%DEST%\osms-nethook.lib" "%DEST%\osms-nethook.exp" 2>nul
endlocal
exit /b 0
