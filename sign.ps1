# SPDX-License-Identifier: AGPL-3.0-or-later
# Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

<#
.SYNOPSIS
  Authenticode-signs and timestamps the AutoGAD MSI.

.DESCRIPTION
  Finds signtool.exe (PATH -> Windows SDK -> local tools\ -> downloads the SDK BuildTools NuGet
  package, ~21 MB, no admin needed), signs, timestamps, then verifies the result and tells you
  plainly whether the signature will actually be trusted on someone else's machine.

  Timestamping matters: without it the signature stops validating the day the certificate expires.
  With it, binaries signed while the cert was valid keep verifying afterwards.

.EXAMPLE
  pwsh -File sign.ps1 -Thumbprint 1A2B3C...        # cert from your certificate store
  pwsh -File sign.ps1 -PfxFile cert.pfx            # prompts for the password
  pwsh -File sign.ps1                              # auto-select (signtool /a)
  pwsh -File sign.ps1 -Path dist\AutoGAD-1.0.1-x64.msi
#>
[CmdletBinding()]
param(
    [string]$Path,                                        # defaults to the newest MSI in dist\
    [string]$Thumbprint,                                  # cert in CurrentUser\My or LocalMachine\My
    [string]$PfxFile,                                     # or a .pfx on disk
    [securestring]$PfxPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$Description  = 'AutoGAD for AutoCAD'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "   $m"  -ForegroundColor Green }
function Write-Info($m) { Write-Host "   $m"  -ForegroundColor Gray }

# ---------------------------------------------------------------- target
if (-not $Path) {
    $Path = Get-ChildItem (Join-Path $root 'dist') -Filter *.msi -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Path) { throw "No MSI found in dist\. Run make.cmd first, or pass -Path." }
}
if (-not (Test-Path $Path)) { throw "Not found: $Path" }
$Path = (Resolve-Path $Path).Path

# ---------------------------------------------------------------- signtool
Write-Step 'Locating signtool.exe'

function Find-SignTool {
    $c = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if ($c) { return $c }

    foreach ($kit in @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")) {
        if (Test-Path $kit) {
            $hit = Get-ChildItem $kit -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
                   Where-Object { $_.FullName -match '\\x64\\' } |
                   Sort-Object FullName -Descending | Select-Object -First 1
            if ($hit) { return $hit.FullName }
        }
    }

    # The SDK package ships x86/x64/arm64 side by side - pick the one this machine can actually run.
    $arch = switch ($env:PROCESSOR_ARCHITECTURE) {
        'ARM64' { 'arm64' }
        'x86'   { 'x86' }
        default { 'x64' }
    }
    $local = Get-ChildItem (Join-Path $root 'tools') -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -match "\\$arch\\" } |
             Sort-Object FullName -Descending | Select-Object -First 1
    if ($local) { return $local.FullName }
    return $null
}

$signtool = Find-SignTool
if (-not $signtool) {
    Write-Info 'Not installed - fetching it from the Windows SDK BuildTools package (~21 MB, no admin)...'
    $toolsDir = Join-Path $root 'tools'
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    $zip = Join-Path $toolsDir 'sdk-buildtools.zip'
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 300 `
        -Uri 'https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools/10.0.26100.1742' `
        -OutFile $zip
    Expand-Archive $zip -DestinationPath (Join-Path $toolsDir 'sdk') -Force
    Remove-Item $zip -Force
    $signtool = Find-SignTool
    if (-not $signtool) { throw 'Could not obtain signtool.exe.' }
}
Write-Ok $signtool

# ---------------------------------------------------------------- sign
Write-Step "Signing $(Split-Path $Path -Leaf)"

$args = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/d', $Description)
if ($Thumbprint) {
    $args += @('/sha1', ($Thumbprint -replace '[^0-9A-Fa-f]', ''))
    Write-Info "Certificate: thumbprint $Thumbprint"
} elseif ($PfxFile) {
    if (-not (Test-Path $PfxFile)) { throw "PFX not found: $PfxFile" }
    if (-not $PfxPassword) { $PfxPassword = Read-Host 'PFX password' -AsSecureString }
    $plain = [Net.NetworkCredential]::new('', $PfxPassword).Password
    $args += @('/f', (Resolve-Path $PfxFile).Path)
    if ($plain) { $args += @('/p', $plain) }
    Write-Info "Certificate: $PfxFile"
} else {
    $args += '/a'
    Write-Info 'Certificate: auto-selected (/a) - pass -Thumbprint to be explicit'
}
$args += $Path

& $signtool @args
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Warning 'Signing failed. Common causes:'
    Write-Warning '  - No code signing certificate installed (error: "No certificates were found...")'
    Write-Warning '  - Certificate expired, or its private key is not available'
    Write-Warning '  - Hardware token not plugged in / PIN not entered (OV+EV certs are token-bound)'
    throw "signtool exited with $LASTEXITCODE"
}
Write-Ok 'Signed and timestamped.'

# ---------------------------------------------------------------- verify
Write-Step 'Verifying'

# /pa = use the Authenticode policy, i.e. judge it the way Windows will on a user's machine.
$verify = & $signtool verify /pa /v $Path 2>&1
$verifyOk = $LASTEXITCODE -eq 0
$verify | ForEach-Object { Write-Info $_ }

Write-Host ''
if ($verifyOk) {
    Write-Host 'TRUSTED: this signature chains to a trusted root - users will see your publisher name.' -ForegroundColor Green
} else {
    Write-Host 'NOT TRUSTED on other machines.' -ForegroundColor Yellow
    Write-Host @'
   The file is signed, but the certificate does not chain to a CA that Windows trusts -
   almost always because it is self-signed. That is fine for testing your pipeline, but
   end users will still get the SmartScreen "unknown publisher" warning.

   For public distribution you need an OV or EV code signing certificate from a CA
   (DigiCert, Sectigo, GlobalSign, ...). Since 2023 the private key must live on a
   hardware token or in a cloud HSM, so plan for that in your build process.
'@ -ForegroundColor Yellow
}
