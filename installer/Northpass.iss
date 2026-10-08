; Compile with Inno Setup 6 after scripts/build.ps1. Reviewed offline engine payload is bundled.
#define AppVersion "0.3.0"
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
LicenseFile=..\THIRD_PARTY_NOTICES.md
[Files]
Source: "..\dist\Northpass\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Northpass"; Filename: "{app}\Northpass.exe"
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
