# SPDX-License-Identifier: AGPL-3.0-or-later
# Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

<#
.SYNOPSIS
  Installs (or removes) the AutoGAD plugin for AutoCAD 2025 and stores the Anthropic API key.

.DESCRIPTION
  Builds AutoGAD.dll, copies it to %APPDATA%\AutoGAD\bin, and registers it for demand-loading under
  every AutoCAD product profile found in HKCU — so AUTOGAD is available in every session without NETLOAD.
  Then prompts for an Anthropic API key, verifies it against the API, and saves it encrypted
  (Windows DPAPI, current user only) in %APPDATA%\AutoGAD\config.json.

.EXAMPLE
  pwsh -File install.ps1                     # build, install, prompt for the key if none is stored
  pwsh -File install.ps1 -SetKey             # only change the stored API key (no build/install)
  pwsh -File install.ps1 -ApiKey sk-ant-...  # non-interactive key
  pwsh -File install.ps1 -SetKey -Provider openai -BaseUrl https://openrouter.ai/api/v1 -ApiKey sk-or-...
  pwsh -File install.ps1 -NoBuild            # install the DLL already in bin\Release
  pwsh -File install.ps1 -Uninstall          # remove registration and installed files
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,          # remove the plugin
    [switch]$SetKey,             # only set/replace the API key
    [switch]$NoBuild,            # skip the build, install the existing bin\Release\AutoGAD.dll
    [switch]$SkipKey,            # install without touching the API key
    [string]$ApiKey,             # supply the key non-interactively
    [ValidateSet('anthropic', 'openai')]
    [string]$Provider,           # which API the key is for (default: the configured one, else anthropic)
    [string]$BaseUrl,            # OpenAI-compatible base URL, e.g. https://openrouter.ai/api/v1
    [switch]$RemoveKey,          # delete the stored key (with -SetKey or -Uninstall)
    [switch]$KeepConfig          # with -Uninstall: keep %APPDATA%\AutoGAD\config.json
)

$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$appDir     = Join-Path $env:APPDATA 'AutoGAD'
$binDir     = Join-Path $appDir 'bin'
$dllPath    = Join-Path $binDir 'AutoGAD.dll'
$configPath = Join-Path $appDir 'config.json'
$acadBase   = 'HKCU:\Software\Autodesk\AutoCAD'
$commands   = @('AUTOGAD', 'AUTOGADKEY', 'AUTOGADSET', 'AUTOGADCFG', 'AUTOGADVER',
                'AUTOGADMEM', 'AUTOGADMEMCLEAR', 'AUTOGADRESET', 'GADEXPORT')

function Write-Step($msg) { Write-Host "`n== $msg" -ForegroundColor Cyan }
function Write-Ok  ($msg) { Write-Host "   $msg" -ForegroundColor Green }
function Write-Info($msg) { Write-Host "   $msg" -ForegroundColor Gray }

# ---------------------------------------------------------------- DPAPI helper
# Same blob format the plugin's DataProtection.cs reads: base64(CryptProtectData(utf8 key)).
Add-Type -Namespace AutoGadSetup -Name Dpapi -MemberDefinition @'
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string plainText)
    {
        byte[] input = System.Text.Encoding.UTF8.GetBytes(plainText);
        DATA_BLOB inBlob = new DATA_BLOB();
        DATA_BLOB outBlob = new DATA_BLOB();
        try
        {
            inBlob.cbData = input.Length;
            inBlob.pbData = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            if (!CryptProtectData(ref inBlob, "AutoGAD API key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out outBlob))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            byte[] result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return System.Convert.ToBase64String(result);
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
'@

# ---------------------------------------------------------------- config helpers
# Two providers share one config.json: Anthropic (apiKeyEnc, model) and any OpenAI-compatible API
# (openaiApiKeyEnc, openaiModel, openaiBaseUrl). "provider" selects the active one.
function Read-Config {
    if (Test-Path $configPath) {
        try { return (Get-Content $configPath -Raw | ConvertFrom-Json -AsHashtable) } catch { }
    }
    return @{}
}

function Write-Config($cfg) {
    New-Item -ItemType Directory -Force -Path $appDir | Out-Null
    $defaults = [ordered]@{
        provider = 'anthropic'; apiKeyEnc = ''; model = 'claude-opus-5'
        openaiApiKeyEnc = ''; openaiModel = 'gpt-5'; openaiBaseUrl = 'https://api.openai.com/v1'
        maxTokens = 16000; effort = 'high'; fallbacks = $true; autoApprove = $false
    }
    foreach ($k in $defaults.Keys) { if (-not $cfg.ContainsKey($k)) { $cfg[$k] = $defaults[$k] } }
    $cfg.Remove('apiKey')      # never leave a plain-text key behind
    $cfg | ConvertTo-Json -Depth 5 | Set-Content -Path $configPath -Encoding UTF8
}

function Get-ActiveProvider {
    $cfg = Read-Config
    if ($cfg.ContainsKey('provider') -and $cfg['provider'] -eq 'openai') { 'openai' } else { 'anthropic' }
}

function Get-KeyField($provider) { if ($provider -eq 'openai') { 'openaiApiKeyEnc' } else { 'apiKeyEnc' } }

function Test-KeyStored($provider) {
    $cfg = Read-Config
    $f = Get-KeyField $provider
    return ($cfg.ContainsKey($f) -and $cfg[$f]) -or
           ($provider -eq 'anthropic' -and $cfg.ContainsKey('apiKey') -and $cfg['apiKey'])
}

function Save-ApiKey([string]$key, [string]$provider, [string]$baseUrl) {
    $cfg = Read-Config
    $cfg[(Get-KeyField $provider)] = [AutoGadSetup.Dpapi]::Protect($key.Trim())
    $cfg['provider'] = $provider
    if ($provider -eq 'openai' -and $baseUrl) { $cfg['openaiBaseUrl'] = $baseUrl.Trim().TrimEnd('/') }
    Write-Config $cfg
}

# Remove one provider's key, or both when no provider is given.
function Remove-ApiKey([string]$provider) {
    $cfg = Read-Config
    if ($cfg.Count -eq 0) { Write-Info "No config file at $configPath"; return }
    if ($provider) { $cfg.Remove((Get-KeyField $provider)) } else { $cfg.Remove('apiKeyEnc'); $cfg.Remove('openaiApiKeyEnc') }
    $cfg.Remove('apiKey')
    Write-Config $cfg
    Write-Ok "Stored API key removed from $configPath"
}

# Cheap key check against GET /models — costs no tokens.
function Test-ApiKey([string]$key, [string]$provider, [string]$baseUrl) {
    if ($provider -eq 'openai') {
        $uri = $baseUrl.Trim().TrimEnd('/') + '/models'
        $headers = @{ 'Authorization' = "Bearer $key" }
    } else {
        $uri = 'https://api.anthropic.com/v1/models?limit=1'
        $headers = @{ 'x-api-key' = $key; 'anthropic-version' = '2023-06-01' }
    }
    try {
        Invoke-WebRequest -Uri $uri -Method Get -UseBasicParsing -TimeoutSec 30 -Headers $headers | Out-Null
        return @{ Ok = $true }
    } catch {
        $code = $null
        if ($_.Exception.PSObject.Properties.Name -contains 'Response' -and $_.Exception.Response) {
            try { $code = [int]$_.Exception.Response.StatusCode } catch { }
        }
        switch ($code) {
            401     { return @{ Ok = $false; Fatal = $true;  Message = 'Rejected by the API (401): this key is not valid.' } }
            403     { return @{ Ok = $false; Fatal = $true;  Message = 'Rejected by the API (403): the key lacks permission.' } }
            default { return @{ Ok = $false; Fatal = $false; Message = "Could not reach the API ($($_.Exception.Message))." } }
        }
    }
}

function Set-ApiKeyInteractive {
    if (-not $script:Provider) {
        $cur = Get-ActiveProvider
        $ans = Read-Host "Provider - [A]nthropic (Claude) or [O]penAI-compatible (OpenAI, OpenRouter, ...) [current: $cur]"
        $script:Provider = if ($ans -match '^[Oo]') { 'openai' } elseif ($ans -match '^[Aa]') { 'anthropic' } else { $cur }
    }
    if ($script:Provider -eq 'openai' -and -not $script:BaseUrl) {
        $cfg = Read-Config
        $def = if ($cfg.ContainsKey('openaiBaseUrl') -and $cfg['openaiBaseUrl']) { $cfg['openaiBaseUrl'] } else { 'https://api.openai.com/v1' }
        $ans = Read-Host "Base URL (OpenAI: https://api.openai.com/v1, OpenRouter: https://openrouter.ai/api/v1) [$def]"
        $script:BaseUrl = if ($ans) { $ans.Trim() } else { $def }
    }

    if ($ApiKey) {
        $key = $ApiKey
    } else {
        if (Test-KeyStored $script:Provider) {
            Write-Info "A $($script:Provider) key is already stored in $configPath."
            Write-Info 'Press Enter to keep it, or paste a new key to replace it.'
        } elseif ($script:Provider -eq 'openai') {
            Write-Info 'Create a key at https://platform.openai.com/api-keys (or https://openrouter.ai/keys)'
        } else {
            Write-Info 'Create a key at https://console.anthropic.com/settings/keys'
        }
        $secure = Read-Host "$($script:Provider) API key" -AsSecureString
        $key = [System.Net.NetworkCredential]::new('', $secure).Password
        if ([string]::IsNullOrWhiteSpace($key)) {
            if (Test-KeyStored $script:Provider) {
                Write-Info 'Keeping the existing key.'
                $cfg = Read-Config; $cfg['provider'] = $script:Provider
                if ($script:Provider -eq 'openai' -and $script:BaseUrl) { $cfg['openaiBaseUrl'] = $script:BaseUrl.TrimEnd('/') }
                Write-Config $cfg
                return
            }
            Write-Warning "No key entered. AutoGAD will ask for one the first time you use the palette (or run AUTOGADKEY)."
            return
        }
    }

    Write-Info 'Verifying the key with the API...'
    $check = Test-ApiKey $key $script:Provider $script:BaseUrl
    if (-not $check.Ok) {
        Write-Warning $check.Message
        if ($check.Fatal) { Write-Warning 'Key not saved.'; return }
        $answer = Read-Host 'Save the key anyway? [y/N]'
        if ($answer -notmatch '^(y|yes)$') { Write-Warning 'Key not saved.'; return }
    } else {
        Write-Ok 'Key verified.'
    }

    Save-ApiKey $key $script:Provider $script:BaseUrl
    Write-Ok "$($script:Provider) key saved (encrypted for $env:USERNAME) in $configPath"
}

# ---------------------------------------------------------------- registry
# Every product profile under R25.0+ (AutoCAD 2025+, .NET 8). Older releases run .NET Framework
# and would show a load error at startup if this assembly were registered there.
function Get-AcadProfiles {
    if (-not (Test-Path $acadBase)) { return @() }
    Get-ChildItem $acadBase -ErrorAction SilentlyContinue | Where-Object {
        $_.PSChildName -match '^R(\d+)' -and [int]$Matches[1] -ge 25
    } | ForEach-Object {
        Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | Where-Object {
            Test-Path (Join-Path $_.PSPath 'Applications')
        }
    }
}

function Register-Plugin {
    $count = 0
    foreach ($prod in Get-AcadProfiles) {
        $app = Join-Path $prod.PSPath 'Applications\AutoGAD'
        New-Item -Path $app -Force | Out-Null
        New-ItemProperty -Path $app -Name 'DESCRIPTION' -Value 'AutoGAD AI assistant' -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $app -Name 'LOADER'      -Value $dllPath              -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $app -Name 'LOADCTRLS'   -Value 14 -PropertyType DWord -Force | Out-Null  # 2=startup|4=command|8=lisp
        New-ItemProperty -Path $app -Name 'MANAGED'     -Value 1  -PropertyType DWord -Force | Out-Null  # .NET assembly

        $cmds = Join-Path $app 'Commands'
        New-Item -Path $cmds -Force | Out-Null
        foreach ($c in $commands) {
            New-ItemProperty -Path $cmds -Name $c -Value $c -PropertyType String -Force | Out-Null
        }
        Write-Ok ("Registered under " + ($prod.PSPath -replace '.*AutoCAD\\', ''))
        $count++
    }
    return $count
}

function Unregister-Plugin {
    $count = 0
    foreach ($prod in Get-AcadProfiles) {
        $app = Join-Path $prod.PSPath 'Applications\AutoGAD'
        if (Test-Path $app) {
            Remove-Item -Path $app -Recurse -Force
            Write-Ok ("Unregistered from " + ($prod.PSPath -replace '.*AutoCAD\\', ''))
            $count++
        }
    }
    return $count
}

# ================================================================ key-only mode
if ($SetKey) {
    Write-Step 'API key'
    if ($RemoveKey) { Remove-ApiKey $Provider } else { Set-ApiKeyInteractive }
    return
}

# ================================================================ uninstall
if ($Uninstall) {
    Write-Step 'Removing AutoGAD'
    $n = Unregister-Plugin
    if ($n -eq 0) { Write-Info 'No registry entries found.' }

    foreach ($stale in @($dllPath, (Join-Path $appDir 'AutoGAD.dll'))) {
        if (Test-Path $stale) {
            try { Remove-Item $stale -Force; Write-Ok "Deleted $stale" }
            catch { Write-Warning "Could not delete $stale (close AutoCAD and re-run): $($_.Exception.Message)" }
        }
    }
    if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force -ErrorAction SilentlyContinue }

    # Legacy autoloader bundle from earlier versions.
    $bundle = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\AutoGAD.bundle'
    if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force -ErrorAction SilentlyContinue; Write-Ok 'Removed legacy .bundle' }

    if ($KeepConfig) {
        Write-Info "Kept $configPath (contains your encrypted API key)."
    } elseif (Test-Path $configPath) {
        $answer = if ($RemoveKey) { 'y' } else { Read-Host "Delete $configPath (your saved API key)? [y/N]" }
        if ($answer -match '^(y|yes)$') { Remove-Item $configPath -Force; Write-Ok 'Config deleted.' }
        else { Write-Info "Kept $configPath" }
    }

    Write-Host "`nAutoGAD removed. Restart AutoCAD to unload it from the current session." -ForegroundColor Cyan
    return
}

# ================================================================ install
Write-Step 'Checking prerequisites'

# The MSI installs the same plugin as an Autodesk bundle. Having both means AutoCAD loads two
# copies of the assembly from different paths, which fails with duplicate command definitions.
foreach ($bundleRoot in @($env:ProgramFiles, $env:APPDATA)) {
    $rival = Join-Path $bundleRoot 'Autodesk\ApplicationPlugins\AutoGAD.bundle'
    if (Test-Path (Join-Path $rival 'Contents\AutoGAD.dll')) {
        Write-Warning "AutoGAD is already installed as a bundle at:`n              $rival"
        Write-Warning "Running this dev installer as well would load the plugin twice. Remove the packaged"
        Write-Warning "version first (Apps and Features -> 'AutoGAD for AutoCAD', or msiexec /x), or use it instead."
        $answer = Read-Host 'Continue anyway? [y/N]'
        if ($answer -notmatch '^(y|yes)$') { Write-Info 'Aborted.'; return }
    }
}

$acadDir = 'C:\Program Files\Autodesk\AutoCAD 2025'
if (-not (Test-Path (Join-Path $acadDir 'acmgd.dll'))) {
    if ($NoBuild) {
        Write-Warning "AutoCAD 2025 not found at $acadDir — installing the prebuilt DLL anyway."
    } else {
        throw "AutoCAD 2025 not found at $acadDir (acmgd.dll missing). Install AutoCAD 2025, or use -NoBuild to install a prebuilt DLL."
    }
} else {
    Write-Ok "AutoCAD 2025 found at $acadDir"
}

if (Get-Process -Name 'acad' -ErrorAction SilentlyContinue) {
    Write-Warning 'AutoCAD is running. If it has already loaded AutoGAD, the DLL is locked — close AutoCAD and re-run.'
}

$sourceDll = Join-Path $root 'bin\Release\AutoGAD.dll'

if (-not $NoBuild) {
    Write-Step 'Building AutoGAD (Release)'

    # Prefer a dotnet that actually has an SDK: the per-user one first, then PATH.
    $dotnet = $null
    foreach ($candidate in @((Join-Path $HOME '.dotnet\dotnet.exe'), (Get-Command dotnet -ErrorAction SilentlyContinue).Source)) {
        if ($candidate -and (Test-Path $candidate)) {
            $sdks = & $candidate --list-sdks 2>$null
            if ($LASTEXITCODE -eq 0 -and $sdks) { $dotnet = $candidate; break }
        }
    }
    if (-not $dotnet) {
        throw "No .NET SDK found (checked $HOME\.dotnet\dotnet.exe and PATH). Install the .NET SDK, or use -NoBuild with a prebuilt DLL."
    }
    Write-Info "Using $dotnet"
    # MSB3277: WindowsBase version conflict between the net8.0 ref pack and AutoCAD's assemblies.
    # Harmless (AutoCAD supplies the runtime), but it buries the real output - demote it to a message.
    & $dotnet build (Join-Path $root 'AutoGAD.csproj') -c Release -nologo -v q -warnAsMessage:MSB3277
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    Write-Ok 'Build succeeded.'
}

if (-not (Test-Path $sourceDll)) { throw "Not found: $sourceDll (run without -NoBuild to build it)." }

Write-Step 'Installing files'
New-Item -ItemType Directory -Force -Path $binDir | Out-Null
try {
    Copy-Item $sourceDll $dllPath -Force
} catch {
    throw "Could not copy the DLL to $dllPath - close AutoCAD (it has the file locked) and re-run. $($_.Exception.Message)"
}
Write-Ok "Installed $dllPath"

# Older versions put the DLL directly in %APPDATA%\AutoGAD and used a .bundle autoloader; clean both up.
$oldDll = Join-Path $appDir 'AutoGAD.dll'
if (Test-Path $oldDll) {
    try { Remove-Item $oldDll -Force; Write-Info 'Removed the old DLL copy from the config folder.' } catch { }
}
$bundleManifest = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\AutoGAD.bundle\PackageContents.xml'
if (Test-Path $bundleManifest) {
    Remove-Item $bundleManifest -Force -ErrorAction SilentlyContinue
    Write-Info 'Neutralised the legacy .bundle autoloader (the registry entry is authoritative).'
}

Write-Step 'Registering with AutoCAD'
$registered = Register-Plugin
if ($registered -eq 0) {
    Write-Warning 'No AutoCAD product profile found in HKCU — is AutoCAD installed for this user? Run AutoCAD once, then re-run this script.'
}

Write-Step 'API key'
if ($SkipKey) {
    Write-Info 'Skipped (-SkipKey). AutoGAD will ask on first use, or run AUTOGADKEY in AutoCAD.'
} elseif ($RemoveKey) {
    Remove-ApiKey $Provider
} else {
    Set-ApiKeyInteractive
}

Write-Host @"

Done. Restart AutoCAD, then:
  AUTOGAD      show the chat palette (also: the AutoGAD ribbon tab)
  AUTOGADKEY   set / replace / remove the API key (Anthropic or OpenAI-compatible)
  AUTOGADSET   model, effort, max tokens, auto-approve
  AUTOGADMEM   what AutoGAD remembers; AUTOGADRESET starts a new conversation
  GADEXPORT    export the drawing to JSON

Installed:  $dllPath
Config:     $configPath   (API key encrypted for $env:USERNAME)
Uninstall:  pwsh -File install.ps1 -Uninstall
"@ -ForegroundColor Cyan
