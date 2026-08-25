@echo off
rem SPDX-License-Identifier: AGPL-3.0-or-later
rem Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

REM ============================================================================
REM  AutoGAD - one-click installer build.
REM
REM  Double-click this file, or run:  make.cmd [version]   (default: version.txt)
REM  For the per-user Inno Setup installer use make_installer.bat instead.
REM  Produces dist\AutoGAD-<version>-x64.msi with the setup wizard + licence page.
REM  Installs the WiX toolset automatically the first time.
REM ============================================================================
setlocal

set "VERSION=%~1"
if "%VERSION%"=="" if exist "%~dp0version.txt" set /p VERSION=<"%~dp0version.txt"
if "%VERSION%"=="" set "VERSION=1.0.1"

REM Prefer PowerShell 7, fall back to Windows PowerShell.
where pwsh >nul 2>&1
if %ERRORLEVEL%==0 (set "PS=pwsh") else (set "PS=powershell")

echo.
echo Building AutoGAD %VERSION% installer...
echo.

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-msi.ps1" -Version %VERSION%
if errorlevel 1 (
    echo.
    echo BUILD FAILED - see the messages above.
    echo.
    pause
    exit /b 1
)

REM Open the output folder so the .msi is right there.
if exist "%~dp0dist" start "" explorer "%~dp0dist"

echo.
pause
