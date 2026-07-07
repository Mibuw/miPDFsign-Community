#define MyAppName      "miPDFsign Community"
#define MyAppVersion   "1.0.0"
#define MyAppPublisher "Wolfgang Mitterbucher"
#define MyAppExeName   "miPDFsignCommunity.exe"
#define MyPublishDir   "publish"

[Setup]
; Own AppId + own install dir + own group → installs SIDE-BY-SIDE with the
; commercial miPDFsign edition (which uses a different AppId and folder).
AppId                   = {{3B528384-34AC-47C4-B477-AA742872F4E3}
AppName                 = {#MyAppName}
AppVersion              = {#MyAppVersion}
AppVerName              = {#MyAppName} {#MyAppVersion}
AppPublisher            = {#MyAppPublisher}
AppCopyright            = Copyright (C) {#MyAppPublisher}

; x86 install – goes to C:\Program Files (x86)\miPDF\miPDFsign Community
; (distinct from the commercial edition's ...\miPDF\miPDFsign folder → parallel install)
DefaultDirName          = {autopf32}\miPDF\{#MyAppName}
DefaultGroupName        = {#MyAppName}
ArchitecturesAllowed    = x86 x64
ArchitecturesInstallIn64BitMode =

; Output
OutputDir               = output
OutputBaseFilename      = miPDFsignCommunity_Setup_{#MyAppVersion}
SetupIconFile           = ..\Assets\miPDFsign.ico
Compression             = lzma2/ultra64
SolidCompression        = yes

; Appearance
WizardStyle             = modern
WizardResizable         = no
ShowLanguageDialog      = no

; Uninstall
UninstallDisplayName    = {#MyAppName}
UninstallDisplayIcon    = {app}\miPDFsign.ico

; Require admin rights for Program Files install
PrivilegesRequired      = admin

; Upgrade behavior
CloseApplications       = yes
RestartApplications     = no

SignTool      = certum $f
SignedUninstaller = yes

[Languages]
; English first = default/fallback; German is auto-selected on German-locale systems
; (ShowLanguageDialog=no picks the language matching the user's locale, else the first entry).
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german";  MessagesFile: "compiler:Languages\German.isl"

[Messages]
; Shown on the first page of the installer
BeveledLabel=miPDFsign Community

[Tasks]
Name: "desktopicon"; \
  Description: "Create desktop shortcut"; \
  GroupDescription: "Additional icons:"

[Files]
; All published output (self-contained .NET 8, no runtime prerequisite)
Source: "{#MyPublishDir}\*"; \
  DestDir: "{app}"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

; App icon – needed for Add/Remove Programs entry
Source: "..\Assets\miPDFsign.ico"; \
  DestDir: "{app}"; \
  Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}";         Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall";            Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";   Filename: "{app}\{#MyAppExeName}"; \
  Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; \
  Description: "Launch {#MyAppName} now"; \
  Flags: nowait postinstall skipifsilent
