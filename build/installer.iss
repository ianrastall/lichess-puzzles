; Compile through build.ps1 so the version and verified publish folder are supplied together.
#ifndef AppVersion
  #error AppVersion must be supplied by build.ps1
#endif

[Setup]
AppId=Jupiter.LichessPuzzles
AppName=Lichess Puzzles
AppVersion={#AppVersion}
AppPublisher=Ian Rastall
DefaultDirName={localappdata}\Programs\Jupiter\LichessPuzzles
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.26100
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=LichessPuzzles-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\LichessPuzzles.App\Assets\app.ico
UninstallDisplayIcon={app}\LichessPuzzles.exe
AppMutex=Local\Jupiter.LichessPuzzles
CloseApplications=no
RestartApplications=no
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Tell Explorer and Start to refresh icons after install and uninstall, so a reinstall
; does not leave a blank Start menu icon.
ChangesAssociations=yes

[Files]
Source: "..\dist\LichessPuzzles\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[InstallDelete]
; Versions before 24 October 2026 put the shortcut in a Jupiter folder; Start now lists the app by name.
Type: files; Name: "{autoprograms}\Jupiter\Lichess Puzzles.lnk"
Type: dirifempty; Name: "{autoprograms}\Jupiter"

[Icons]
Name: "{autoprograms}\Lichess Puzzles"; Filename: "{app}\LichessPuzzles.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\app.ico"; AppUserModelID: "Jupiter.LichessPuzzles"

[Run]
Filename: "{app}\LichessPuzzles.exe"; Description: "Open Lichess Puzzles"; Flags: nowait postinstall skipifsilent

; The puzzle store, download, players, history and settings live in
; %LOCALAPPDATA%\Jupiter\LichessPuzzles and are kept on uninstall.
