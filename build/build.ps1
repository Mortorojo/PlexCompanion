# PlexCompanion - build script (PowerShell)
# Compiles src\PlexCompanion.cs into dist\PlexCompanion.exe
# Requires: .NET Framework 4.x C# compiler (csc.exe)
#   Default: C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

$ErrorActionPreference = "Stop"
Set-Location -LiteralPath (Join-Path $PSScriptRoot '..')

$CSC = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $CSC)) {
    Write-Error "csc.exe not found at $CSC. Install .NET Framework 4.x and re-run."
    exit 1
}

if (-not (Test-Path "dist")) { New-Item -ItemType Directory -Path "dist" | Out-Null }

Write-Host "[build] Compiling src\PlexCompanion.cs -> dist\PlexCompanion.exe"
& $CSC /nologo /target:winexe /out:dist\PlexCompanion.exe `
    /win32icon:src\PlexCompanion.ico `
    src\PlexCompanion.cs

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed (exit $LASTEXITCODE)."
    exit 1
}

Write-Host "[build] OK  ->  dist\PlexCompanion.exe"
