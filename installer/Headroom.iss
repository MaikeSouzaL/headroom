; Instalador do Headroom (Inno Setup 6).
; Gerado por build\publish.ps1, que passa /DPublishDir=<pasta do dotnet publish>.

#define AppName "Headroom"
#define AppVersion "1.0.0"
#define AppPublisher "Headroom"
#define AppExe "Headroom.exe"
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{6E1E3D0A-9B8B-4C5E-9D1A-3C7B2F4A8E10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename=Headroom-Setup-{#AppVersion}
SetupIconFile=..\src\Headroom.App\Assets\headroom.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; O app roda como asInvoker; o instalador precisa de admin só para gravar em Arquivos de Programas.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0.17763

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#PublishDir}\Rules\*"; DestDir: "{app}\Rules"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove as tarefas agendadas criadas pelo app (se existirem).
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Headroom\Limpeza agendada"" /F"; Flags: runhidden; RunOnceId: "DelCleanupTask"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Headroom\Alerta de espaço livre"" /F"; Flags: runhidden; RunOnceId: "DelSpaceTask"
