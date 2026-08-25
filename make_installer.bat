@echo off
rem SPDX-License-Identifier: AGPL-3.0-or-later
rem Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

setlocal
cd /d "%~dp0"

rem ============================================================================
rem  AutoGAD - one-click installer build (Inno Setup, per-user, no admin).
rem
rem  Double-click, or run:  make_installer.bat
rem  Bumps the patch version in version.txt, stamps it into AutoGAD.csproj and
rem  bundle\PackageContents.xml (stamp-version.ps1), builds AutoGAD.dll (Release)
rem  and produces dist\AutoGADSetup.exe. Needs a .NET SDK and Inno Setup 6.
rem  (make.cmd builds the per-machine MSI instead; do not install both.)
rem ============================================================================

rem Prefer PowerShell 7, fall back to Windows PowerShell.
where pwsh >nul 2>&1
if %ERRORLEVEL%==0 (set "PS=pwsh") else (set "PS=powershell")

rem Auto-increment patch version so every installer build is a newer version
set VERSION=1.0.0
if exist version.txt set /p VERSION=<version.txt
for /f "tokens=1-3 delims=." %%a in ("%VERSION%") do (
    set MAJOR=%%a
    set MINOR=%%b
    set /a PATCH=%%c+1
)
set VERSION=%MAJOR%.%MINOR%.%PATCH%
>version.txt echo %VERSION%
echo Building AutoGAD installer version %VERSION%

rem Keep AutoGAD.csproj / bundle\PackageContents.xml in sync with version.txt
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0stamp-version.ps1" -Version %VERSION%
if errorlevel 1 exit /b 1

rem Build the plugin (Release). MSB3277 is the harmless WindowsBase conflict with AutoCAD's assemblies.
where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet not found on PATH. Install the .NET SDK from https://dotnet.microsoft.com/download
    exit /b 1
)
dotnet build AutoGAD.csproj -c Release -nologo -v q -warnAsMessage:MSB3277
if errorlevel 1 (
    echo BUILD FAILED - see the messages above.
    exit /b 1
)

rem Locate Inno Setup compiler
set ISCC=
for %%p in ("%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%ProgramFiles%\Inno Setup 6\ISCC.exe" "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe") do (
    if not defined ISCC if exist %%p set "ISCC=%%~p"
)
if not defined ISCC for /f "delims=" %%i in ('where ISCC 2^>nul') do if not defined ISCC set "ISCC=%%i"
if not defined ISCC (
    echo ERROR: Inno Setup ISCC.exe not found. Install from https://jrsoftware.org/isinfo.php
    exit /b 1
)

if not exist dist mkdir dist
"%ISCC%" /Q /DMyAppVersion=%VERSION% Installer.iss
if errorlevel 1 exit /b 1
echo Installer built: dist\AutoGADSetup.exe (v%VERSION%)
