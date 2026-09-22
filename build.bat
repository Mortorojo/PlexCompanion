@echo off
setlocal
REM -----------------------------------------------------------------------------
REM  PlexCompanion - build script (Windows, no install needed)
REM  Compiles src\PlexCompanion.cs into dist\PlexCompanion.exe
REM  Requires: .NET Framework 4.x C# compiler (csc.exe), present by default in:
REM    C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
REM -----------------------------------------------------------------------------

cd /d "%~dp0"

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [build] csc.exe not found at default location.
    echo [build] Install .NET Framework 4.x (or VS Build Tools) and re-run.
    exit /b 1
)

if not exist dist mkdir dist

echo [build] Compiling src\PlexCompanion.cs -> dist\PlexCompanion.exe
"%CSC%" /nologo /target:winexe /out:dist\PlexCompanion.exe ^
    /win32icon:src\PlexCompanion.ico ^
    src\PlexCompanion.cs

if errorlevel 1 (
    echo [build] FAILED.
    exit /b 1
)

echo [build] OK  ->  dist\PlexCompanion.exe
endlocal
pause
