; Instalador do tHg Optimization (Inno Setup 6), no estilo dos apps conhecidos: uma tela com o botão INSTALAR,
; barra de progresso e o app abre sozinho no fim. Gerado pelo GitHub Actions (publicar.yml), por publicar.bat ou
; por GERAR-INSTALADOR.bat. Compilar à mão: iscc installer\tHgOptimization.iss [/DAppVersion=x.y.z] /DSourceExe=..\dist\tHgOptimization.exe
; Imagens do assistente: docs/gerar-imagens-instalador.py

#ifndef SourceExe
  #define SourceExe "..\dist\tHgOptimization.exe"
#endif
; Sem /DAppVersion, a versão vem do próprio .exe (que a recebeu do <Version> do .csproj): nunca há número fixo aqui.
#ifndef AppVersion
  #define VerMajor 0
  #define VerMinor 0
  #define VerRev 0
  #define VerBuild 0
  #expr GetVersionComponents(AddBackslash(SourcePath) + SourceExe, VerMajor, VerMinor, VerRev, VerBuild)
  #define AppVersion Str(VerMajor) + "." + Str(VerMinor) + "." + Str(VerRev)
#endif

[Setup]
AppId={{6F2C1E9A-7B4D-4C1E-9E3A-0D5B7A1C9E42}
AppName=tHg Optimization
AppVersion={#AppVersion}
AppVerName=tHg Optimization {#AppVersion}
AppPublisher=tHg
AppCopyright=tHg · baseado no RESET EXPLODE (MIT)
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Instalador do tHg Optimization
DefaultDirName={autopf}\tHg Optimization
DefaultGroupName=tHg Optimization
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=tHgOptimization-Setup-{#AppVersion}
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\tHgOptimization.exe
UninstallDisplayName=tHg Optimization
WizardStyle=modern
WizardImageFile=wizard-1x.bmp,wizard-2x.bmp
WizardSmallImageFile=wizard-small-1x.bmp,wizard-small-2x.bmp
WizardImageStretch=no
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
; Um clique: sem escolher pasta, sem menu Iniciar separado, sem tela de revisão e sem tela final.
DisableWelcomePage=no
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
ShowLanguageDialog=no

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Messages]
ptbr.WelcomeLabel1=tHg Optimization
ptbr.WelcomeLabel2=Desempenho, segurança e diagnóstico para o seu PC gamer.%n%nO instalador vai:%n  • instalar o tHg Optimization {#AppVersion}%n  • criar atalhos na Área de Trabalho e no Menu Iniciar%n  • instalar o driver PawnIO (temperatura e consumo da CPU)%n%nNada no Windows é alterado até você escolher, dentro do app. Leva menos de um minuto.
ptbr.SetupWindowTitle=Instalar tHg Optimization
ptbr.StatusExtractFiles=Copiando o tHg Optimization…

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LEIA-ME.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\tHg Optimization"; Filename: "{app}\tHgOptimization.exe"
Name: "{autodesktop}\tHg Optimization"; Filename: "{app}\tHgOptimization.exe"

[Run]
; PawnIO pelo winget (oficial, assinado). Sem winget ou sem internet, o app mostra o botão CORRIGIR depois.
Filename: "{code:WingetPath}"; Parameters: "install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements"; \
  StatusMsg: "Instalando o driver de sensores (PawnIO)…"; Flags: runhidden waituntilterminated; Check: HasWinget and not IsUpdate
; Abre o app no fim, como os instaladores conhecidos (não em instalação silenciosa /SILENT).
Filename: "{app}\tHgOptimization.exe"; Flags: nowait skipifsilent shellexec
; Atualização pelo próprio app (/VERYSILENT /UPDATE): abre a versão nova sozinho, na bandeja se estava na bandeja.
Filename: "{app}\tHgOptimization.exe"; Parameters: "--after-update"; Flags: nowait; Check: IsUpdate

; As configurações do usuário (%AppData%\tHgOptimization) ficam ao desinstalar: guardam os valores originais
; dos ajustes do Windows. Para desfazer os ajustes, use "Desfazer tudo" no app ANTES de desinstalar.

[Code]
function WingetPath(Param: String): String;
begin
  Result := ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe');
end;

function IsUpdate: Boolean;
begin
  Result := WizardSilent and (Pos('/UPDATE', Uppercase(GetCmdTail)) > 0);
end;

function HasWinget: Boolean;
begin
  Result := FileExists(WingetPath(''));
end;

{ "Iniciar com o Windows" é uma tarefa agendada que guarda o caminho do .exe que a criou. Se foi uma cópia antiga
  (ex.: o .exe portátil em Downloads), o Windows continuaria abrindo essa cópia: aponta a tarefa para a versão instalada. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  if Exec('schtasks.exe', '/Query /TN "tHg Optimization"', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
    Exec('schtasks.exe', '/Create /TN "tHg Optimization" /TR "\"' + ExpandConstant('{app}') + '\tHgOptimization.exe\" --tray" /SC ONLOGON /RL HIGHEST /DELAY 0000:20 /F',
      '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { Na tela de boas-vindas o botão vira INSTALAR: é a única tela antes do progresso. }
  if CurPageID = wpWelcome then
    WizardForm.NextButton.Caption := 'Instalar';
end;
