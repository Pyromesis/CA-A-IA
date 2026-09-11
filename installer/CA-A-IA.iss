; CA-A-IA — Instalador Inno Setup (firma CA).
; Compilar con: installer\Build-Installer.ps1 (publica la app, resuelve la
; versión desde Directory.Build.props y llama a ISCC con los defines).
;   ISCC.exe installer\CA-A-IA.iss /DPublishDir="..." /DAppVersion="0.1.0" [/DUSE_CODESIGN] [/S"casign=..."]
;
; Firma de código (Authenticode, editor "CA"): si se define USE_CODESIGN, el
; instalador y el desinstalador se firman con la herramienta "casign"
; (ver Build-Installer.ps1: usa CA_PFX_PATH/CA_PFX_PASSWORD + timestamp RFC 3161).
; Sin certificado, el script compila igual pero sin firma Authenticode.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\\publish\\win-x64"
#endif

#define MyAppName "CA-A-IA"
#define MyAppExe "CA-A-IA.Presentation.exe"
#define MyAppPublisher "CA"
#define MyAppCopyright "Copyright © CA"
#define MyAppId "{{C036EC05-9EF2-4C7D-AE28-DB21688D0225}}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright={#MyAppCopyright}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription={#MyAppName} — Programación asistida por IA (por CA)
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Sin memoria de instalaciones previas: el grupo y las tareas son siempre los
; del script (así el acceso directo se crea garantizado, también al actualizar
; desde versiones con otro nombre de grupo).
UsePreviousGroup=no
UsePreviousTasks=no
PrivilegesRequired=lowest
; Solo por línea de comandos (/ALLUSERS): en uso normal siempre por usuario
; (nunca pide UAC ni va a ProgramData por sorpresa).
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
SetupIconFile=..\\src\\CA-A-IA.Presentation\\Assets\\CA-A-IA.ico
UninstallDisplayIcon={app}\\{#MyAppExe}
LicenseFile=License-es.txt
OutputDir=Output
OutputBaseFilename={#MyAppName}-Setup-{#AppVersion}
Compression=lzma2/ultra
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto
#ifdef USE_CODESIGN
SignTool=casign
SignedUninstaller=yes
#endif

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Acceso en el escritorio activado por defecto: siempre hay una forma visible de abrir la app.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
; Publicación self-contained win-x64 (generada por Build-Installer.ps1).
Source: "{#PublishDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"

[Icons]
Name: "{group}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExe}"; IconFilename: "{app}\\{#MyAppExe}"
Name: "{autodesktop}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; Permite lanzar "CA-A-IA" desde Ejecutar (Win+R) sin tocar el PATH.
Root: HKCU; Subkey: "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\CA-A-IA.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\\{#MyAppExe}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\CA-A-IA.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Auto-actualización (/UPDATE=1): reabre la app también en silencioso.
Filename: "{app}\\{#MyAppExe}"; Flags: nowait; Check: IsSelfUpdate

[UninstallDelete]
; Los datos de usuario (%LocalAppData%\CA-A-IA: BD, ajustes, secretos) se
; conservan a propósito tras desinstalar.

[InstallDelete]
; Actualización desde la versión intermedia (nombre CA-I-AI): restos con el
; nombre viejo que el Files nuevo ya no sobrescribe. La carpeta entera del
; grupo anterior se elimina (el grupo nuevo se recrea siempre, ver
; UsePreviousGroup=no arriba). También grupos legacy por-máquina de pruebas
; elevadas (fallan en silencio si no hay permiso: no rompen nada).
Type: filesandordirs; Name: "{autoprograms}\\CA-I-AI"
Type: filesandordirs; Name: "{commonprograms}\\CA-A-IA"
Type: filesandordirs; Name: "{commonprograms}\\CA-I-AI"
Type: files; Name: "{app}\\CA-I-AI.Presentation.*"
Type: files; Name: "{autodesktop}\\CA-I-AI.lnk"

[Code]
// Limpia la clave App Paths del nombre intermedio (otra subclave: el
// uninsdeletekey nuevo no la tocaría).
function IsSelfUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;
procedure DeleteObsoleteFiles(const Dir: string);
var
  FindRec: TFindRec;
  Path: string;
begin
  if FindFirst(Dir + '\*', FindRec) then
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        Path := Dir + '\' + FindRec.Name;
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          DeleteObsoleteFiles(Path);
          RemoveDir(Path);
        end
        else if (CompareText(FindRec.Name, 'unins000.exe') <> 0)
          and (CompareText(FindRec.Name, 'unins000.dat') <> 0) then
          DeleteFile(Path);
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

// Actualización limpia: si se instala en la carpeta POR DEFECTO y ya existe
// (versión anterior), se vacía antes de instalar —salvo el desinstalador
// previo, cuyo .dat se conserva para que el nuevo lo amplíe—. En una carpeta
// ELEGIDA POR EL USUARIO jamás se borra nada (solo los patrones legacy de
// arriba): sus archivos no son nuestros.
procedure CurStepChanged(CurStep: TSetupStep);
var
  DefaultDir: string;
begin
  if CurStep = ssInstall then
  begin
    RegDeleteKeyIncludingSubkeys(HKCU,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\CA-I-AI.exe');
    DefaultDir := ExpandConstant('{localappdata}\Programs\{#MyAppName}');
    if (CompareText(WizardDirValue, DefaultDir) = 0) and DirExists(WizardDirValue) then
      DeleteObsoleteFiles(WizardDirValue);
  end;
end;
