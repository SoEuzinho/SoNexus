; Instalador do SoNexus (Inno Setup 6).
; Gerar:  build.ps1 (publica o app e roda o ISCC). Saída: installer\Output\SoNexus-Setup-<versão>.exe
;
; "Seguro" aqui quer dizer:
; - instala só o SoNexus, sem baixar nada da internet e sem programas extras;
; - vai para "Arquivos de Programas" (só administradores alteram os arquivos depois);
; - desinstalador completo em Configurações › Aplicativos, que também remove a tarefa "Iniciar com o Windows";
; - o hash SHA-256 do instalador é publicado junto, para conferir que ninguém alterou o arquivo.

#ifndef AppVersion
  #define AppVersion "0.13.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6F0C2B7E-4E3A-4C59-9C1B-5D2E7A3F9B10}
AppName=SoNexus
AppVersion={#AppVersion}
AppVerName=SoNexus {#AppVersion}
AppPublisher=SoStudio (criado por SoEuzinho)
AppCopyright=© SoStudio · criado por SoEuzinho
VersionInfoVersion={#AppVersion}
VersionInfoCompany=SoStudio
VersionInfoDescription=Instalador do SoNexus
DefaultDirName={autopf}\SoNexus
DefaultGroupName=SoNexus
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
SetupIconFile=..\SoNexus\Assets\SoNexus.ico
UninstallDisplayIcon={app}\SoNexus.exe
UninstallDisplayName=SoNexus
; Página "Contrato de licença": a pessoa só continua marcando "Eu aceito" (Termos de Uso e Privacidade).
LicenseFile=..\SoNexus\TERMOS-E-PRIVACIDADE.txt
OutputDir=Output
OutputBaseFilename=SoNexus-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; GroupDescription: "Atalhos:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SoNexus"; Filename: "{app}\SoNexus.exe"
Name: "{group}\Desinstalar o SoNexus"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SoNexus"; Filename: "{app}\SoNexus.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SoNexus.exe"; Description: "Abrir o SoNexus agora"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
; Fecha o app (inclusive o que está na bandeja) e remove o "Iniciar com o Windows".
Filename: "{sys}\taskkill.exe"; Parameters: "/IM SoNexus.exe /F"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""SoNexus"" /F"; Flags: runhidden; RunOnceId: "DelTask"

[Code]
// Atualizar com o app aberto na bandeja: fecha antes de copiar os arquivos.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM SoNexus.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;

// Desinstalar: pergunta se apaga também configurações, registros e backups.
// O próprio SoNexus chama com /DELETEDATA ou /KEEPDATA (a pessoa já escolheu dentro do app).
function HasParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DeleteData: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if HasParam('/DELETEDATA') then DeleteData := True
  else if HasParam('/KEEPDATA') or UninstallSilent then DeleteData := False
  else DeleteData := MsgBox('Apagar também as configurações, os registros e os backups do SoNexus?' + #13#10#13#10 +
    'Escolha "Não" se pretende instalar de novo e quer manter suas escolhas.', mbConfirmation, MB_YESNO) = IDYES;
  if DeleteData then
  begin
    DelTree(ExpandConstant('{commonappdata}\SoNexus'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\SoNexus'), True, True, True);
  end;
end;
