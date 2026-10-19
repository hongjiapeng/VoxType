#ifndef MyAppName
  #define MyAppName "VoxType"
#endif
#ifndef MyAppId
  #define MyAppId "{{28F435F3-A41D-4C3F-B5EB-077D060BD364}"
#endif
#define MyAppExeName "VoxType.App.exe"
#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif
#ifndef SourceDir
  #error SourceDir must point to the verified portable publish directory.
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher=VoxType contributors
AppPublisherURL=https://github.com/hongjiapeng/VoxType
AppSupportURL=https://github.com/hongjiapeng/VoxType/issues
DefaultDirName={localappdata}\Programs\VoxType
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.22000
CloseApplications=yes
CloseApplicationsFilter=VoxType.App.exe
RestartApplications=no
OutputDir={#OutputDir}
OutputBaseFilename=VoxType-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\VoxType.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Assets\AppIcon.ico

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Remove only the obsolete service source shipped by v0.1.0. Models live elsewhere.
Type: files; Name: "{app}\services\R2T2.Server\*.cs"
Type: files; Name: "{app}\services\R2T2.Server\R2T2.Server.csproj"

[Icons]
Name: "{group}\VoxType"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--settings"; WorkingDir: "{app}"
Name: "{autodesktop}\VoxType"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--settings"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--settings"; Description: "Launch VoxType"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
