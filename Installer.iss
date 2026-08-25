; SPDX-License-Identifier: AGPL-3.0-or-later
; Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

; AutoGAD - Inno Setup script. Build with make_installer.bat (reads version.txt).
;
; Per-user install, no admin rights:
;   %APPDATA%\AutoGAD\bin\AutoGAD.dll           the plugin (same layout install.ps1 uses)
;   HKCU\Software\Autodesk\AutoCAD\R25+\<product>\Applications\AutoGAD   demand-load registration
;   %APPDATA%\AutoGAD\apikey.pending             optional API key, encrypted by the plugin on first load
;
; Registry demand-loading (not the .bundle autoloader) on purpose: it works even when the AutoCAD
; profile has APPAUTOLOAD=0, which switches every bundle off. Only R25.0+ (AutoCAD 2025+, .NET 8)
; profiles are registered - older releases run .NET Framework and cannot load this assembly.
;
; Do not combine with the MSI (build-msi.ps1): two copies of the assembly from different paths make
; AutoCAD fail on duplicate command definitions. Setup warns if it finds the MSI's bundle.

#ifndef MyAppVersion
  #define VerFile FileOpen("version.txt")
  #define MyAppVersion Trim(FileRead(VerFile))
  #expr FileClose(VerFile)
#endif

[Setup]
AppId={{B6E9F1A2-7C44-4E0B-9A1D-A07A60000002}
AppName=AutoGAD
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppPublisher=Dmitry Dobrovolskiy
PrivilegesRequired=lowest
DefaultDirName={userappdata}\AutoGAD
DisableDirPage=yes
DefaultGroupName=AutoGAD
DisableProgramGroupPage=yes
LicenseFile=installer\License.rtf
OutputDir=dist
OutputBaseFilename=AutoGADSetup
Compression=lzma2
SolidCompression=yes
UninstallFilesDir={app}\bin
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=AutoGAD for AutoCAD

[Files]
Source: "bin\Release\AutoGAD.dll"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "README.md";               DestDir: "{app}\bin"; Flags: ignoreversion
Source: "version.txt";             DestDir: "{app}\bin"; Flags: ignoreversion
Source: "LICENSE";                 DestDir: "{app}\bin"; Flags: ignoreversion

[UninstallDelete]
; Only the plugin files - config.json (encrypted key), memory\ and history\ are kept for a reinstall.
Type: filesandordirs; Name: "{app}\bin"

[Messages]
FinishedLabel=AutoGAD has been installed.%n%nStart AutoCAD: an AutoGAD tab appears on the ribbon, and AUTOGAD opens the chat palette. You can change the API key any time via AutoGAD > API key (command AUTOGADKEY).

[Code]
const
  AcadBase = 'Software\Autodesk\AutoCAD';
  Commands = 'AUTOGAD,AUTOGADKEY,AUTOGADSET,AUTOGADMEM,AUTOGADMEMCLEAR,AUTOGADRESET,AUTOGADVER,AUTOGADCFG,GADEXPORT';

var
  KeyPage: TInputQueryWizardPage;
  ProviderPage: TInputOptionWizardPage;

function JsonEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

function IsAcadRunning: Boolean;
var
  Locator, Svc, Procs: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Svc := Locator.ConnectServer('.', 'root\CIMV2');
    Procs := Svc.ExecQuery('SELECT Name FROM Win32_Process WHERE Name = ''acad.exe''');
    Result := Procs.Count > 0;
  except
    Result := False;
  end;
end;

{ "R25.0" -> 25; anything unparsable -> 0 }
function ReleaseMajor(const Name: String): Integer;
var
  S: String;
  P: Integer;
begin
  Result := 0;
  if (Length(Name) < 2) or (UpperCase(Copy(Name, 1, 1)) <> 'R') then exit;
  S := Copy(Name, 2, Length(Name) - 1);
  P := Pos('.', S);
  if P > 0 then S := Copy(S, 1, P - 1);
  Result := StrToIntDef(S, 0);
end;

{ Walk HKCU\Software\Autodesk\AutoCAD\R*\<product> and register/unregister in every profile that has an Applications key. }
function ForEachAcadProduct(Register: Boolean): Integer;
var
  Releases, Products, Cmds: TArrayOfString;
  i, j, k: Integer;
  Prod, AppKey, Dll: String;
begin
  Result := 0;
  Dll := ExpandConstant('{app}\bin\AutoGAD.dll');
  if not RegGetSubkeyNames(HKCU, AcadBase, Releases) then exit;
  for i := 0 to GetArrayLength(Releases) - 1 do
  begin
    if ReleaseMajor(Releases[i]) < 25 then continue;    { .NET 8 plugin: AutoCAD 2025+ only }
    if not RegGetSubkeyNames(HKCU, AcadBase + '\' + Releases[i], Products) then continue;
    for j := 0 to GetArrayLength(Products) - 1 do
    begin
      Prod := AcadBase + '\' + Releases[i] + '\' + Products[j];
      if not RegKeyExists(HKCU, Prod + '\Applications') then continue;
      AppKey := Prod + '\Applications\AutoGAD';
      if Register then
      begin
        RegWriteStringValue(HKCU, AppKey, 'DESCRIPTION', 'AutoGAD AI assistant');
        RegWriteStringValue(HKCU, AppKey, 'LOADER', Dll);
        RegWriteDWordValue(HKCU, AppKey, 'LOADCTRLS', 14);   { 2=startup | 4=command | 8=lisp }
        RegWriteDWordValue(HKCU, AppKey, 'MANAGED', 1);      { .NET assembly }
        Cmds := StringSplit(Commands, [','], stAll);
        for k := 0 to GetArrayLength(Cmds) - 1 do
          RegWriteStringValue(HKCU, AppKey + '\Commands', Cmds[k], Cmds[k]);
      end
      else if RegKeyExists(HKCU, AppKey) then
        RegDeleteKeyIncludingSubkeys(HKCU, AppKey);
      Result := Result + 1;
    end;
  end;
end;

function InitializeSetup: Boolean;
var
  Bundle: String;
begin
  Result := True;
  while IsAcadRunning do
  begin
    if MsgBox('AutoCAD is running. Close it first, then click Retry (an open AutoCAD locks the plugin file).',
              mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      exit;
    end;
  end;
  Bundle := ExpandConstant('{commonpf}\Autodesk\ApplicationPlugins\AutoGAD.bundle\Contents\AutoGAD.dll');
  if FileExists(Bundle) then
    if MsgBox('AutoGAD is also installed for all users as an Autodesk bundle (from the MSI):' + #13#10 + Bundle + #13#10#13#10 +
              'Having both makes AutoCAD load the plugin twice and fail with duplicate commands. ' +
              'Uninstall "AutoGAD for AutoCAD" from Apps and Features first.' + #13#10#13#10 + 'Continue anyway?',
              mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
end;

procedure InitializeWizard;
begin
  ProviderPage := CreateInputOptionPage(wpWelcome,
    'AI provider', 'Which API should AutoGAD talk to?',
    'Both need your own API key. You can switch providers later in AutoGAD > Settings.', True, False);
  ProviderPage.Add('Anthropic - Claude models (api.anthropic.com)');
  ProviderPage.Add('OpenAI-compatible - OpenAI, OpenRouter or any compatible server');
  ProviderPage.Values[0] := True;

  KeyPage := CreateInputQueryPage(ProviderPage.ID,
    'API key', 'Optional - you can also set it later inside AutoCAD (AUTOGADKEY)',
    'Paste your API key. Leave empty to skip. It is stored encrypted for your Windows account in ' +
    '%APPDATA%\AutoGAD\config.json. The base URL is used only for OpenAI-compatible providers ' +
    '(OpenAI: https://api.openai.com/v1, OpenRouter: https://openrouter.ai/api/v1).');
  KeyPage.Add('API key:', True);
  KeyPage.Add('Base URL (OpenAI-compatible only):', False);
  KeyPage.Values[1] := 'https://api.openai.com/v1';
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = KeyPage.ID then
    KeyPage.Edits[1].Enabled := ProviderPage.Values[1];
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Key: String;
  Provider: String;
  N: Integer;
begin
  if CurStep <> ssPostInstall then exit;

  N := ForEachAcadProduct(True);
  if N = 0 then
    MsgBox('No AutoCAD 2025+ profile was found under HKEY_CURRENT_USER, so the plugin could not be registered for auto-loading.' + #13#10 +
           'Start AutoCAD once (this creates the profile), then run this installer again - or load it manually with NETLOAD:' + #13#10 +
           ExpandConstant('{app}\bin\AutoGAD.dll'), mbInformation, MB_OK);

  Key := Trim(KeyPage.Values[0]);
  if Key = '' then exit;
  if ProviderPage.Values[1] then Provider := 'openai' else Provider := 'anthropic';
  ForceDirectories(ExpandConstant('{userappdata}\AutoGAD'));
  { The plugin reads this file on first load, stores the key DPAPI-encrypted in config.json and deletes it. }
  SaveStringToFile(ExpandConstant('{userappdata}\AutoGAD\apikey.pending'),
    '{"provider":"' + Provider + '","apiKey":"' + JsonEscape(Key) + '","baseUrl":"' + JsonEscape(Trim(KeyPage.Values[1])) + '"}', False);
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  while IsAcadRunning do
    if MsgBox('AutoCAD is running. Close it first, then click Retry.', mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      exit;
    end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    ForEachAcadProduct(False);
    DeleteFile(ExpandConstant('{app}\apikey.pending'));
  end;
end;
