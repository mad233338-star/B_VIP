; ============================================================================================
;  B_VIP Tool - desktop installer (Inno Setup 6)
;  Brand: Bashir Al-Farhan
;
;  Build steps:
;    1. powershell -ExecutionPolicy Bypass -File deploy\publish.ps1
;    2. iscc installer\B_VIP-Desktop.iss
;
;  Output: artifacts\dist\B_VIP_Tool_Setup.exe
;
;  The desktop is installed per machine into "Program Files\B_VIP Tool" so that every
;  Windows user of the machine shares one copy. User settings live in %APPDATA%\B_VIP.
; ============================================================================================

#define AppName        "B_VIP Tool"
#define AppShortName   "B_VIP"
#define AppPublisher   "Bashir Al-Farhan"
#define AppVersion     GetEnv("B_VIP_VERSION")
#if AppVersion == ""
  #define AppVersion "1.0.0"
#endif
#define AppExeName     "B_VIP.Desktop.exe"
#define PayloadDir     "..\artifacts\installer-payload"

[Setup]
AppId={{9E4B0F52-3C7D-4A51-9D2E-6B43F0C1A7E4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=license.txt
OutputDir=..\artifacts\dist
OutputBaseFilename=B_VIP_Tool_Setup
SetupIconFile=..\src\B_VIP.Desktop\Assets\b_vip.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
; Shared writable folder for exported diagnostics; the client never writes into Program Files.
Name: "{commonappdata}\{#AppShortName}"; Permissions: users-modify

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Bundled backend tools: the Samsung Odin engines (heimdall.exe + libusb) and the Xiaomi Mi
; Assistant payload folder. Shipped explicitly as well as by the payload tree, so the tabs work
; offline out of the box.
Source: "{#PayloadDir}\Tools\Xiaomi\*"; DestDir: "{app}\Tools\Xiaomi"; Flags: ignoreversion skipifsourcedoesntexist
; Samsung Odin engines (odin4.exe / heimdall.exe + their DLLs). Shipped explicitly so the
; flashing tab always has its engine even if the payload layout changes.
Source: "{#PayloadDir}\Tools\Samsung\*"; DestDir: "{app}\Tools\Samsung"; Flags: ignoreversion skipifsourcedoesntexist
; Default server address; copied to %APPDATA%\B_VIP\settings.json the first time the client runs.
Source: "..\deploy\appsettings.Desktop.sample.json"; DestDir: "{app}"; DestName: "appsettings.json"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "..\docs\USER_GUIDE.md"; DestDir: "{app}\docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\docs\ARABIC_GUIDE.md"; DestDir: "{app}\docs"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} - User guide"; Filename: "{app}\docs\USER_GUIDE.md"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: quicklaunchicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings under %APPDATA%\B_VIP and exports under Documents\B_VIP are intentionally left in
; place; remove them manually if needed.

[Code]
// Warn when the .NET 8 Desktop Runtime is missing: the client is published framework-dependent.
function InitializeSetup(): Boolean;
var
  Versions: TArrayOfString;
  I: Integer;
begin
  Result := True;
  // Any installed 8.x desktop runtime satisfies the client; keying on one exact patch version
  // wrongly warned users who had 8.0.10 or newer.
  if RegGetSubkeyNames(HKLM,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
      Versions) then
  begin
    for I := 0 to GetArrayLength(Versions) - 1 do
    begin
      if Copy(Versions[I], 1, 2) = '8.' then
      begin
        Exit;
      end;
    end;
  end;

  if MsgBox('The .NET 8 Desktop Runtime was not detected on this machine.' + #13#10 +
            'B_VIP Tool needs it to start. Download it from https://dot.net and run this setup again.',
            mbInformation, MB_OKCANCEL) = IDCANCEL then
  begin
    Result := False;
  end;
end;
