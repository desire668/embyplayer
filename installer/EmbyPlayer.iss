; EmbyPlayer Inno Setup 脚本
; 单脚本两产物：用 iscc /DSC 或 iscc /DFD 切换变体
;   SC = Self-Contained，自带 .NET 9 运行时（约 150MB）
;   FD = Framework-Dependent，需用户自行安装 .NET 9 Desktop Runtime（约 50MB）

#ifndef SC
  #ifndef FD
    #error 必须定义变体：iscc installer\EmbyPlayer.iss /DSC  或  /DFD
  #endif
#endif

#ifdef SC
  #define Variant     "SC"
  #define PublishDir   "..\dist\publish-sc"
  #define VariantName "自带 .NET 9 运行时"
#else
  #define Variant     "FD"
  #define PublishDir   "..\dist\publish-fd"
  #define VariantName "需自行安装 .NET 9 Desktop Runtime"
#endif

#define AppName    "EmbyPlayer"
#define AppVersion "1.0.1"
#define AppURL     "https://github.com/desire668/embyplayer"

[Setup]
AppId={{8E2A7E5B-9B47-4F2C-8D6A-0D6D5F1A1234}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion} ({#Variant})
AppPublisher=desire668
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppVersion}
VersionInfoCompany=desire668
VersionInfoDescription={#AppName} Installer ({#Variant})
DefaultDirName={autopf}\EmbyPlayer
DefaultGroupName=EmbyPlayer
UninstallDisplayIcon={app}\EmbyPlayer.exe
UninstallDisplayName={#AppName} {#AppVersion}
OutputDir=..\dist
OutputBaseFilename=EmbyPlayer-Setup-x64-{#Variant}-{#AppVersion}
SetupIconFile=..\src\EmbyPlayer\Assets\app-icon.ico
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=no
AllowCancelDuringInstall=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english";    MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加选项:"

[Files]
; 整个 publish 目录打包（包含 mpv.exe、portable_config、VC++ runtime、icon 等）
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\EmbyPlayer"; Filename: "{app}\EmbyPlayer.exe"; IconFilename: "{app}\app-icon.ico"
Name: "{group}\卸载 EmbyPlayer"; Filename: "{uninstallexe}"
Name: "{userdesktop}\EmbyPlayer"; Filename: "{app}\EmbyPlayer.exe"; IconFilename: "{app}\app-icon.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\EmbyPlayer.exe"; Description: "启动 EmbyPlayer"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 清理崩溃日志
Type: files; Name: "{tmp}\EmbyPlayer.crash.log"
; 清理运行期产生的日志目录（如未来引入）
Type: dirifempty; Name: "{app}\Logs"

[Code]
#ifdef FD
// 检测 .NET 9 Desktop Runtime 是否已安装（系统级 + 用户级两处）
function IsDotNet9DesktopInstalled: Boolean;
var
  Path1, Path2: String;
  FindRec: TFindRec;
  Done: Boolean;
begin
  Result := False;
  Path1 := ExpandConstant('{pf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  Path2 := ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App');
  Done := False;
  // 系统级
  if (not Done) and DirExists(Path1) then begin
    if FindFirst(Path1 + '\*', FindRec) then begin
      try
        repeat
          if (FindRec.Attributes and 16) <> 0 then begin
            if (Length(FindRec.Name) >= 2) and (Copy(FindRec.Name, 1, 2) = '9.') then begin
              Result := True;
              Done := True;
            end;
          end;
        until Done or (not FindNext(FindRec));
      finally
        FindClose(FindRec);
      end;
    end;
  end;
  // 用户级
  if (not Done) and DirExists(Path2) then begin
    if FindFirst(Path2 + '\*', FindRec) then begin
      try
        repeat
          if (FindRec.Attributes and 16) <> 0 then begin
            if (Length(FindRec.Name) >= 2) and (Copy(FindRec.Name, 1, 2) = '9.') then begin
              Result := True;
              Done := True;
            end;
          end;
        until Done or (not FindNext(FindRec));
      finally
        FindClose(FindRec);
      end;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if (CurStep = ssPostInstall) and (not IsDotNet9DesktopInstalled) then begin
    if MsgBox(
        '未检测到 .NET 9 Desktop Runtime。' + #13#10 +
        'EmbyPlayer 需要该运行时才能启动。' + #13#10 + #13#10 +
        '是否打开浏览器前往下载页安装？',
        mbConfirmation, MB_YESNO) = IDYES then begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/9.0', '', '', SW_SHOW, ewNoWait, ErrorCode);
    end;
  end;
end;
#endif
