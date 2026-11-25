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
; Upgrading: running a newer setup over an older one stops the service, replaces the
; program and starts the service again. The registration, the key and gateway.toml
; are left as they were. gateway.toml is never installed or removed by setup: it is
; customer IT's file.

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
; A first install creates the service. It starts once the gateway is registered.
Filename: "{app}\{#Exe}"; Parameters: "install"; Flags: runhidden waituntilterminated; \
  StatusMsg: "Installing the AL Workbench Gateway service..."; Check: not ServiceExisted
; An upgrade starts the service it stopped.
Filename: "{sys}\net.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden waituntilterminated; \
  StatusMsg: "Starting the AL Workbench Gateway service..."; Check: ServiceWasRunning
; Registering prints the key fingerprint the Admin compares before approving, so it
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
{ 0 = not installed, 1 = installed and stopped, 2 = running. sc query exits with
  1060 when there is no such service. }
var
  ResultCode: Integer;
begin
  Result := 0;
  if not Exec(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then Exit;
  if ResultCode <> 0 then Exit;
  Result := 1;
  { "sc query" prints STATE ... RUNNING; findstr exits 0 when it finds it. }
  if Exec(ExpandConstant('{cmd}'), '/c sc.exe query {#ServiceName} | findstr /c:"RUNNING"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
     and (ResultCode = 0) then
    Result := 2;
end;

function ServiceExisted(): Boolean;
begin
  Result := ExistedBefore;
end;

function ServiceWasRunning(): Boolean;
begin
  Result := RunningBefore;
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
begin
  State := ServiceState();
  ExistedBefore := State > 0;
  RunningBefore := State = 2;
  Result := True;
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
  ResultCode: Integer;
begin
  Result := '';
  { An upgrade replaces the program, so the service must not be holding it. net stop
    waits until the service has stopped. }
  if RunningBefore then
    Exec(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
