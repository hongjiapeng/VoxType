#ifndef MyAppName
  #define MyAppName "VoxType"
#endif
#ifndef MyAppId
  #define MyAppId "{{28F435F3-A41D-4C3F-B5EB-077D060BD364}"
#endif
#define MyAppExeName "VoxType.App.exe"
#ifndef AppVersion
  #define AppVersion "0.1.2"
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
; v0.1.1 bundled the HTTP host. Local mode now uses the shared library.
Type: files; Name: "{app}\R2T2.Server.*"
Type: files; Name: "{app}\Microsoft.AspNetCore*.dll"
; Current desktop dependencies are copied back by [Files], including AI.Abstractions.
Type: files; Name: "{app}\Microsoft.Extensions.*.dll"
Type: files; Name: "{app}\aspnetcorev2_inprocess.dll"
Type: files; Name: "{app}\Microsoft.JSInterop.dll"
Type: files; Name: "{app}\Microsoft.Net.Http.Headers.dll"
Type: files; Name: "{app}\System.Diagnostics.EventLog*.dll"
Type: files; Name: "{app}\System.Formats.Cbor.dll"
Type: files; Name: "{app}\System.Security.Cryptography.Pkcs.dll"
Type: files; Name: "{app}\System.Security.Cryptography.Xml.dll"
Type: files; Name: "{app}\System.Threading.RateLimiting.dll"
Type: files; Name: "{app}\scripts\prepare-r2t2.ps1"
Type: files; Name: "{app}\scripts\start-r2t2.ps1"
Type: files; Name: "{app}\scripts\r2t2-common.ps1"
Type: files; Name: "{app}\scripts\r2t2-assets.json"

[Icons]
Name: "{group}\VoxType"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--settings"; WorkingDir: "{app}"
Name: "{autodesktop}\VoxType"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--settings"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--settings"; Description: "Launch VoxType"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
