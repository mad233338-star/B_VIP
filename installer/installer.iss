; B_VIP Tool — production installer
;
; Packages the published desktop build (artifacts\desktop), the bundled platform-tools and the
; optional driver bundle into one setup executable:
;
;     artifacts\dist\B_VIP_Tool_Setup.exe
;
; Build it after deploy\publish.ps1 has produced artifacts\desktop:
;
;     iscc installer\installer.iss
;
; The driver folder is optional on purpose: vendor USB drivers are licensed by their vendor, so the
; administrator drops the matching installers into artifacts\desktop\drivers before packaging. When
; the folder is absent the Drivers & Prerequisites panel simply opens the vendor download pages.

#define AppName "B_VIP Tool"
#define AppVersion "1.0.0"
#define AppPublisher "B_VIP"
#define AppExeName "B_VIP.Desktop.exe"
#define SourceDir "..\artifacts\desktop"

[Setup]
AppId={{8F3C1B42-5A6D-4F2E-9C77-5D1B0A9F0001}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\B_VIP Tool
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\dist
OutputBaseFilename=B_VIP_Tool_Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile=license.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "drivers"; Description: "Install the bundled USB drivers after setup"; GroupDescription: "Drivers:"; Flags: unchecked

[Files]
; The whole published desktop folder: the executable, its dependencies, appsettings.json,
; platform-tools\adb.exe + fastboot.exe and drivers\ when the bundle was staged.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "license.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
; pnputil installs every INF the bundle carries; a setup EXE dropped in the same folder is run by
; the Drivers & Prerequisites panel instead, because each vendor needs its own silent switch.
Filename: "{sys}\pnputil.exe"; Parameters: "/add-driver ""{app}\drivers"" /install /subdirs"; \
    Flags: runhidden waituntilterminated; Tasks: drivers; StatusMsg: "Installing bundled USB drivers…"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\data"

[Code]
{ Warns when the publish output is missing so the build fails loudly instead of producing an empty setup. }
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not FileExists(ExpandConstant('{#SourceDir}\{#AppExeName}')) then
  begin
    MsgBox('The published desktop build was not found:' + #13#10 +
           ExpandConstant('{#SourceDir}\{#AppExeName}') + #13#10#13#10 +
           'Run deploy\publish.ps1 first, then compile this script again.',
           mbError, MB_OK);
    Result := False;
  end;
end;
