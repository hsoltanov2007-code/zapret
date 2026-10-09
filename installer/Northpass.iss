; Compile with Inno Setup 6 after scripts/build.ps1. Reviewed offline engine payload is bundled.
#define AppVersion "0.7.0"
[Setup]
AppId={{292E32E3-4A84-4F03-B41E-64F5B3C05E96}
AppName=Northpass
AppVersion={#AppVersion}
AppPublisher=Northpass contributors
DefaultDirName={autopf}\Northpass
DefaultGroupName=Northpass
OutputDir=..\dist\installer
OutputBaseFilename=Northpass-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\Northpass.App\Assets\Northpass.ico
UninstallDisplayIcon={app}\Northpass.exe
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
DisableProgramGroupPage=yes
DisableDirPage=yes
WizardStyle=modern
SetupLogging=yes
[InstallDelete]
; Product-owned helper files are replaced as a unit; user data is elsewhere.
Type: filesandordirs; Name: "{app}\broker"
; Narrow known v0.5 product files only. Keep user preferences and old protected drivers untouched.
Type: files; Name: "{app}\Northpass.Engine.Zapret2.dll"
Type: files; Name: "{app}\engine-payload\zapret2-offline.zip"
Type: files; Name: "{app}\profiles\zapret2-reviewed-example.json"
Type: files; Name: "{app}\profiles\example-template.json"
Type: files; Name: "{app}\docs\third-party-source\zapret2-1.0.5.2-source.zip"
[Files]
Source: "..\dist\Northpass\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Northpass"; Flags: dontcreatekey uninsdeletevalue
[Icons]
Name: "{group}\Northpass"; Filename: "{app}\Northpass.exe"
[Run]
; Migrate this user away from the legacy elevated startup task.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Northpass-{code:CurrentUserSid}"" /F"; Flags: runhidden
Filename: "{app}\Northpass.exe"; Description: "Launch Northpass"; Flags: postinstall skipifsilent shellexec runasoriginaluser
[UninstallRun]
; Remove this user's task only; user settings and protected engine revisions stay intact.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Northpass-{code:CurrentUserSid}"" /F"; Flags: runhidden; RunOnceId: "RemoveNorthpassAutostart"
[Code]
function CurrentUserSid(Param: String): String;
var
  Shell, Exec: Variant;
begin
  { Uses the logged-on installer's identity, matching the app's per-user task name. }
  Shell := CreateOleObject('WScript.Shell');
  Exec := Shell.Exec(ExpandConstant('{sys}\whoami.exe') + ' /user /fo csv /nh');
  Result := Exec.StdOut.ReadAll;
  { whoami CSV is "domain\user","S-1-..."; extract the quoted final field. }
  Delete(Result, 1, Pos('","', Result) + 2);
  Result := Copy(Result, 1, Pos('"', Result) - 1);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Root: String;
begin
  if CurStep = ssPostInstall then
  begin
    Root := '"' + ExpandConstant('{app}') + '"';
    { Set ownership before removing inherited access. Fixed product root only. }
    if not Exec(ExpandConstant('{sys}\icacls.exe'), Root + ' /setowner *S-1-5-32-544 /T /Q', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      RaiseException('Northpass could not protect installation ownership.');
    if not Exec(ExpandConstant('{sys}\icacls.exe'), Root + ' /reset /T /Q', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      RaiseException('Northpass could not reset installation permissions.');
    if not Exec(ExpandConstant('{sys}\icacls.exe'), Root + ' /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" /T /Q', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      RaiseException('Northpass could not protect installation components.');
    if not Exec(ExpandConstant('{app}\broker\Northpass.Broker.exe'), '--install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      RaiseException('Northpass could not prepare bundled components. Repair the installation.');
  end;
end;
