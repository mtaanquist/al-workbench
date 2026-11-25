; The AL Workbench Gateway setup program (#1778), built with Inno Setup 6.
;
; It copies the gateway into C:\Program Files\AL Workbench Gateway, has the gateway
; install its own Windows service, and can register it with AL Workbench. Everything
; that touches the service, the key or the settings is the gateway's own commands
; (install, register, uninstall), so the setup program and a person at a command
; prompt do exactly the same thing. See .design/on-prem-gateway.md, "Setup program".
;
; Built by staging.yml with:
;   ISCC.exe /DAppVersion=<version> /DSourceDir=<publish folder> /DOutputDir=<folder> gateway.iss
;
; Upgrading: running a newer setup over an older one stops the service (waiting while
; it finishes what it is doing), replaces the program, makes sure the service account is
; a local administrator (Business Central's cmdlets require it) and starts the service again.
; A service installed by hand from another folder is refused, not upgraded. The registration, the key and gateway.toml
; are left as they were. Setup never replaces or removes gateway.toml, which is
; customer IT's file; when there is none, "init" writes a starter one that lists this
; computer's service tiers, allowed only to be reported on.

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\bin\Release\net10.0-windows\win-x64\publish"
#endif
#ifndef OutputDir
  #define OutputDir "out"
#endif

#define ServiceName "ALWorkbenchGateway"
#define Exe "ALWorkbench.Gateway.exe"

[Setup]
; Never change the AppId: it is how a newer setup finds the installed gateway.
AppId={{C0436549-6336-4B7E-8BDF-AEAF9B6C36FD}
AppName=AL Workbench Gateway
AppVersion={#AppVersion}
AppVerName=AL Workbench Gateway {#AppVersion}
AppPublisher=Consortio
DefaultDirName={autopf}\AL Workbench Gateway
; The gateway refuses to run from a folder ordinary users can change, and
; Program Files is safe by default, so the folder is not offered as a choice.
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=ALWorkbench.Gateway.Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#Exe}
UninstallDisplayName=AL Workbench Gateway
InfoAfterFile=after-install.txt
; The service is stopped by PrepareToInstall below; nothing else holds the program open.
CloseApplications=no

[Files]
Source: "{#SourceDir}\{#Exe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\gateway.example.toml"; DestDir: "{app}"; Flags: ignoreversion

[Run]
; Installing the service (first install) and starting it again (upgrade) happen in
; CurStepChanged below, where a failure can be shown. Registering prints the key fingerprint the Admin compares before approving, so it
; runs in a window that stays open until a key is pressed.
Filename: "{cmd}"; Parameters: "/c ""{code:RegisterCommand}"""; Flags: waituntilterminated; \
  StatusMsg: "Registering with AL Workbench..."; Check: WantsRegister

[UninstallRun]
; Removes the service, the gateway's key and its settings. Logs and gateway.toml stay.
Filename: "{app}\{#Exe}"; Parameters: "uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "GatewayUninstall"

[Code]
var
  RegisterPage: TInputQueryWizardPage;
  ExistedBefore: Boolean;
  RunningBefore: Boolean;

function ServiceState(): Integer;
{ 0 = not installed, 1 = stopped, 2 = starting, stopping or running. sc query
  exits with 1060 when there is no such service. The state is matched by its
  number (2 start pending, 3 stop pending, 4 running), which sc prints the same
  in every Windows language. }
var
  ResultCode: Integer;
begin
  Result := 0;
  if not Exec(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then Exit;
  if ResultCode <> 0 then Exit;
  Result := 1;
  if Exec(ExpandConstant('{cmd}'), '/c sc.exe query {#ServiceName} | findstr /r /c:"STATE *: [234] "', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
     and (ResultCode = 0) then
    Result := 2;
end;

function InstalledProgramPath(): String;
{ The program the existing service runs, without its quotes and the "run" argument. }
var
  ImagePath: String;
  I: Integer;
begin
  Result := '';
  if not RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\{#ServiceName}', 'ImagePath', ImagePath) then Exit;
  ImagePath := Trim(ImagePath);
  if Copy(ImagePath, 1, 1) = '"' then
  begin
    Delete(ImagePath, 1, 1);
    I := Pos('"', ImagePath);
    if I > 0 then ImagePath := Copy(ImagePath, 1, I - 1);
  end;
  Result := ImagePath;
end;

procedure InitializeWizard();
begin
  RegisterPage := CreateInputQueryPage(wpSelectDir,
    'Register with AL Workbench',
    'Connect this computer to your AL Workbench.',
    'In AL Workbench, go to Administration > Gateways, choose Add gateway and copy the registration code. ' +
    'Leave both boxes empty to register later from a command prompt.');
  RegisterPage.Add('AL Workbench address (e.g. https://tools.cronus.com):', False);
  RegisterPage.Add('Registration code:', False);
end;

function InitializeSetup(): Boolean;
var
  State: Integer;
  Existing, Expected: String;
begin
  State := ServiceState();
  ExistedBefore := State > 0;
  RunningBefore := State = 2;
  Result := True;
  if ExistedBefore then
  begin
    { A gateway installed by hand from another folder would keep running the old
      program after this setup copied a new one here. }
    Existing := InstalledProgramPath();
    Expected := ExpandConstant('{autopf}\AL Workbench Gateway\{#Exe}');
    if (Existing <> '') and (CompareText(Existing, Expected) <> 0) then
    begin
      MsgBox('The AL Workbench Gateway service on this computer runs ' + Existing + '.' + #13#10#13#10 +
        'This setup installs the gateway in ' + ExtractFileDir(Expected) + '. To move it there, run ' +
        '"ALWorkbench.Gateway uninstall" from the old folder first, then run this setup again and register the gateway anew.',
        mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { An upgrade keeps the registration it has. }
  Result := (PageID = RegisterPage.ID) and ExistedBefore;
end;

function HasUnsafeCharacters(const Value: String): Boolean;
{ The values go on a command line: refuse anything cmd.exe would read as syntax. }
var
  I: Integer;
begin
  Result := False;
  for I := 1 to Length(Value) do
    if Pos(Value[I], '"&|<>^%!()') > 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Url, Code: String;
begin
  Result := True;
  if CurPageID <> RegisterPage.ID then Exit;
  Url := Trim(RegisterPage.Values[0]);
  Code := Trim(RegisterPage.Values[1]);
  if (Url = '') and (Code = '') then Exit;
  if (Url = '') or (Code = '') then
  begin
    MsgBox('Fill in both the address and the registration code, or leave both empty to register later.', mbError, MB_OK);
    Result := False;
  end
  else if CompareText(Copy(Url, 1, 8), 'https://') <> 0 then
  begin
    MsgBox('The address must start with https://, e.g. https://tools.cronus.com.', mbError, MB_OK);
    Result := False;
  end
  else if HasUnsafeCharacters(Url) or HasUnsafeCharacters(Code) or (Pos(' ', Url) > 0) or (Pos(' ', Code) > 0) then
  begin
    MsgBox('The address or the code contains a character that does not belong there. Copy them again from AL Workbench.', mbError, MB_OK);
    Result := False;
  end;
end;

function WantsRegister(): Boolean;
begin
  Result := (not ExistedBefore) and (Trim(RegisterPage.Values[0]) <> '') and (Trim(RegisterPage.Values[1]) <> '');
end;

function RegisterCommand(Param: String): String;
begin
  Result := '"' + ExpandConstant('{app}\{#Exe}') + '" register --url ' + Trim(RegisterPage.Values[0]) +
    ' --code ' + Trim(RegisterPage.Values[1]) + ' & echo. & pause';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode, Waited: Integer;
begin
  Result := '';
  if not RunningBefore then Exit;
  { An upgrade replaces the program, so the service must have stopped first. A
    gateway in the middle of restarting service tiers can take several minutes to
    stop, longer than net stop waits, so the state is polled for up to 15 minutes. }
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Waited := 0;
  while (ServiceState() = 2) and (Waited < 900) do
  begin
    Sleep(2000);
    Waited := Waited + 2;
  end;
  if ServiceState() = 2 then
    Result := 'The AL Workbench Gateway service did not stop within 15 minutes, so nothing was changed. ' +
      'It may be in the middle of restarting service tiers. Try again later.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  { A starter gateway.toml listing this computer's tiers, inventory only, so they show
    up in AL Workbench straight away. init leaves an existing file alone. }
  WizardForm.StatusLabel.Caption := 'Writing the starter gateway settings...';
  Exec(ExpandConstant('{app}\{#Exe}'), 'init', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not ExistedBefore then
  begin
    { A first install creates the service. It starts once the gateway is registered. }
    WizardForm.StatusLabel.Caption := 'Installing the AL Workbench Gateway service...';
    if not Exec(ExpandConstant('{app}\{#Exe}'), 'install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      MsgBox('The gateway was copied, but its Windows service could not be installed. Open a command prompt with ' +
        '"Run as administrator", go to ' + ExpandConstant('{app}') + ' and run "ALWorkbench.Gateway install" to see why.',
        mbError, MB_OK);
  end
  else
  begin
    { An upgrade: install on an installed gateway only makes sure its account is a
      local administrator, which Business Central's cmdlets require and which gateways
      installed before that was known may lack. }
    WizardForm.StatusLabel.Caption := 'Checking the gateway service account...';
    if not Exec(ExpandConstant('{app}\{#Exe}'), 'install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      MsgBox('The gateway was upgraded, but its service account could not be added to the local Administrators group, ' +
        'which Business Central requires. Open a command prompt with "Run as administrator", go to ' + ExpandConstant('{app}') +
        ' and run "ALWorkbench.Gateway install" to see why.', mbError, MB_OK);
  end;
  if ExistedBefore and RunningBefore then
  begin
    { An upgrade starts the service it stopped. }
    WizardForm.StatusLabel.Caption := 'Starting the AL Workbench Gateway service...';
    if not Exec(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      MsgBox('The gateway was upgraded, but its service did not start. Start "AL Workbench Gateway" in Services, ' +
        'and look in C:\ProgramData\AL Workbench Gateway\logs if it stops again.', mbError, MB_OK);
  end;
end;
