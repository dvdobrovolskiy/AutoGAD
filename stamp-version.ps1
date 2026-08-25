# SPDX-License-Identifier: AGPL-3.0-or-later
# Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

<#
.SYNOPSIS
  Stamps one version number into every file that carries it (called by make_installer.bat).

.DESCRIPTION
  version.txt is the single source of truth. This writes the same value into
    AutoGAD.csproj               <Version>            -> assembly/file version (AUTOGADVER, palette)
    bundle\PackageContents.xml   AppVersion / Version -> Autodesk bundle manifest (MSI)

.EXAMPLE
  pwsh -File stamp-version.ps1 -Version 1.0.3
  pwsh -File stamp-version.ps1              # uses version.txt
#>
[CmdletBinding()]
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $Version) { $Version = (Get-Content (Join-Path $root 'version.txt') -Raw).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3; got '$Version'." }

$csproj = Join-Path $root 'AutoGAD.csproj'
$text = [IO.File]::ReadAllText($csproj)
$text = $text -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
[IO.File]::WriteAllText($csproj, $text)

$manifest = Join-Path $root 'bundle\PackageContents.xml'
$text = [IO.File]::ReadAllText($manifest)
$text = $text -replace 'AppVersion="[^"]*"', "AppVersion=`"$Version`""
$text = $text -replace '(<ComponentEntry[^>]*?)Version="[^"]*"', "`$1Version=`"$Version`""
[IO.File]::WriteAllText($manifest, $text)

Write-Host "Stamped version $Version into AutoGAD.csproj and bundle\PackageContents.xml"
