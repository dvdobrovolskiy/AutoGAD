# SPDX-License-Identifier: AGPL-3.0-or-later
# Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

<#
.SYNOPSIS
  Builds AutoGAD.dll and packages it into a distributable per-machine MSI.

.DESCRIPTION
  Output: dist\AutoGAD-<version>-x64.msi

  The MSI installs the Autodesk Autoloader bundle to
  %ProgramFiles%\Autodesk\ApplicationPlugins\AutoGAD.bundle\, which AutoCAD scans at startup.
  No registry demand-load entries and no custom actions, so uninstall is a clean file removal.

  Requires the WiX v5 CLI:  dotnet tool install --global wix --version 5.0.2
  (WiX v6+ requires accepting the Open Source Maintenance Fee EULA - v5 does not.)

.EXAMPLE
  pwsh -File build-msi.ps1
  pwsh -File build-msi.ps1 -Version 1.2.0
  pwsh -File build-msi.ps1 -NoBuild          # package the existing bin\Release\AutoGAD.dll
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [switch]$NoBuild,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

$root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$installer = Join-Path $root 'installer'
$bundleSrc = Join-Path $root 'bundle'
$dllPath   = Join-Path $root 'bin\Release\AutoGAD.dll'
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }

function Write-Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "   $m"  -ForegroundColor Green }
function Write-Info($m) { Write-Host "   $m"  -ForegroundColor Gray }

if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "-Version must look like 1.2.3 (MSI versions are numeric); got '$Version'."
}

# ---------------------------------------------------------------- wix
Write-Step 'Checking the WiX toolset'

# WiX v5 on purpose: v6+ refuses to build until you accept the Open Source Maintenance Fee EULA,
# which is a paid licence for commercial use. v5 is free and produces the same MSI.
$wixPin = '5.0.2'

# Deliberately look for a v5 wix specifically. A machine-wide WiX v6/v7 may well be on PATH, and
# picking that up silently would fail the build demanding EULA acceptance.
function Find-Wix5 {
    $candidates = @(
        (Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe')
        (Get-Command wix -ErrorAction SilentlyContinue).Source
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) {
            $v = (& $c --version 2>$null) -join ''
            if ($v -match '^5\.') { return [pscustomobject]@{ Path = $c; Version = $v } }
        }
    }
    return $null
}

$found = Find-Wix5
if (-not $found) {
    $other = (Get-Command wix -ErrorAction SilentlyContinue).Source
    if ($other) {
        $ov = (& $other --version 2>$null) -join ''
        Write-Info "Found wix $ov at $other - not v5, so installing v$wixPin alongside it."
    } else {
        Write-Info "WiX not found - installing v$wixPin (one-time, needs internet)..."
    }
    $dn = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dn) { throw 'dotnet not found on PATH; cannot auto-install WiX.' }
    & $dn tool install --global wix --version $wixPin --configfile (Join-Path $root 'NuGet.config') 2>&1 | Out-Null
    $found = Find-Wix5
    if (-not $found) { throw "WiX v$wixPin install failed. Run manually: dotnet tool install --global wix --version $wixPin" }
}

$wix = $found.Path
Write-Ok "wix $($found.Version) ($wix)"

# The wizard UI (welcome + licence + progress + finish pages) lives in this extension.
$haveUi = (& $wix extension list --global 2>$null) -match "WixToolset\.UI\.wixext $wixPin"
if (-not $haveUi) {
    Write-Info "Adding the WixUI extension (provides the wizard and licence page)..."
    & $wix extension add --global "WixToolset.UI.wixext/$wixPin" 2>&1 | Out-Null
}
Write-Ok 'Wizard UI extension ready.'

# ---------------------------------------------------------------- build
if (-not $NoBuild) {
    Write-Step 'Building AutoGAD (Release)'
    $dotnet = $null
    foreach ($c in @((Join-Path $HOME '.dotnet\dotnet.exe'), (Get-Command dotnet -ErrorAction SilentlyContinue).Source)) {
        if ($c -and (Test-Path $c)) {
            $sdks = & $c --list-sdks 2>$null
            if ($LASTEXITCODE -eq 0 -and $sdks) { $dotnet = $c; break }
        }
    }
    if (-not $dotnet) { throw 'No .NET SDK found. Install one, or use -NoBuild with a prebuilt DLL.' }
    Write-Info "Using $dotnet"

    # MSB3277: harmless WindowsBase conflict between the net8.0 ref pack and AutoCAD's assemblies.
    # Compiler warnings are captured rather than printed - they belong to the code, not to packaging.
    $buildLog = & $dotnet build (Join-Path $root 'AutoGAD.csproj') -c Release -nologo -v q -warnAsMessage:MSB3277 2>&1
    if ($LASTEXITCODE -ne 0) {
        $buildLog | ForEach-Object { Write-Host $_ }
        throw 'Build failed.'
    }
    $warnings = @($buildLog | Select-String -Pattern ': warning ').Count
    Write-Ok ("Build succeeded" + $(if ($warnings) { " ($warnings compiler warnings hidden - run install.ps1 to see them)" } else { '.' }))
}

if (-not (Test-Path $dllPath)) { throw "Not found: $dllPath (run without -NoBuild to build it)." }

# ---------------------------------------------------------------- version sync
# The bundle manifest is shipped verbatim, so stamp the same version into it that the MSI carries.
Write-Step 'Preparing the bundle manifest'
$stageDir = Join-Path $root 'obj\bundle-stage'
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

$manifest = Get-Content (Join-Path $bundleSrc 'PackageContents.xml') -Raw
$manifest = $manifest -replace 'AppVersion="[^"]*"', "AppVersion=`"$Version`""
$manifest = $manifest -replace '(<ComponentEntry[^>]*?)Version="[^"]*"', "`$1Version=`"$Version`""
$manifest | Set-Content (Join-Path $stageDir 'PackageContents.xml') -Encoding UTF8
Write-Ok "Stamped AppVersion=$Version"

# ---------------------------------------------------------------- package
Write-Step 'Building the MSI'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$msi = Join-Path $OutDir "AutoGAD-$Version-x64.msi"

# Build the -d values first: PowerShell will not concatenate "Name=" with a parenthesised expression.
$licenseRtf = Join-Path $installer 'License.rtf'
$wxs        = Join-Path $installer 'AutoGAD.wxs'

& $wix build $wxs `
    -arch x64 `
    -ext WixToolset.UI.wixext/5.0.2 `
    -d "ProductVersion=$Version" `
    -d "BundleSourceDir=$stageDir" `
    -d "DllSourcePath=$dllPath" `
    -d "LicenseRtf=$licenseRtf" `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw 'wix build failed.' }

# .wixpdb holds build symbols - useful for debugging, not something to hand to users.
Move-Item ([IO.Path]::ChangeExtension($msi, '.wixpdb')) (Join-Path $root 'obj') -Force -ErrorAction SilentlyContinue

$size = [math]::Round((Get-Item $msi).Length / 1KB, 1)
Write-Ok "$msi  ($size KB)"

Write-Host @"

Distributable package ready:
  $msi

Install:          msiexec /i "$msi"
Silent install:   msiexec /i "$msi" /qn
Uninstall:        msiexec /x "$msi"

Installs to %ProgramFiles%\Autodesk\ApplicationPlugins\AutoGAD.bundle (all users).
Each user sets their own API key on first use (AUTOGADKEY).

Before shipping, sign it:
  signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a "$msi"
"@ -ForegroundColor Cyan
