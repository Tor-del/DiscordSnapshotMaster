#define AppName "APEX DSM"
#define LegacyAppName "APEX Discord Snapshot Master"
#define AppExe LegacyAppName + ".exe"
#ifndef AppVersion
  #define AppVersion "1.2.6"
#endif
#ifndef PublishDir
  #error PublishDir must point to the self-contained Windows publish folder.
#endif

[Setup]
AppId={{06CEF7B9-42A2-4C63-B8F3-50B81474E355}
AppName={#AppName}
AppVersion={#AppVersion}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
DefaultDirName={localappdata}\Programs\{#LegacyAppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=APEX-Discord-Snapshot-Master-Setup-x64
SetupIconFile=..\gui\Assets\App.ico
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LanguageDetectionMethod=none
WizardStyle=modern dynamic
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[InstallDelete]
Type: files; Name: "{group}\{#LegacyAppName}.lnk"
Type: files; Name: "{userprograms}\{#LegacyAppName}\{#LegacyAppName}.lnk"
Type: files; Name: "{userdesktop}\{#LegacyAppName}.lnk"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, "&", "&&")}}"; Flags: nowait postinstall skipifsilent
