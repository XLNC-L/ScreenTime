; ScreenTime 安装脚本（Inno Setup 6.3+）
;
; 构建：
;   dotnet publish ScreenTime.WinUI\ScreenTime.WinUI.csproj -c Release -r win-x64 ^
;       --self-contained false -o ScreenTime-WinUI
;   ISCC.exe /DAppVersion=1.0.0 installer\ScreenTime.iss
;
; 版本号从命令行注入（/DAppVersion=x.y.z），未注入时用下面的默认值。

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "ScreenTime"
#define AppPublisher "XLNC"
#define AppExeName "ScreenTime.exe"
; TODO 发布前替换成实际仓库地址
#define RepoUrl "https://github.com/XLNC-L/ScreenTime"

[Setup]
; AppId 必须保持不变，否则升级会变成"装了两个"
AppId={{8F3C1D2A-6B4E-4A9C-9F1D-2E7B5C3A8D41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL={#RepoUrl}
AppUpdatesURL={#RepoUrl}/releases

; 装到用户目录：全程不需要管理员权限、不弹 UAC
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=

; 仅支持 64 位
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\ScreenTime.WinUI\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
LicenseFile=LICENSE.txt
MinVersion=10.0.17763

; 安装/卸载时尝试关闭正在运行的实例
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinese"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
; 发布目录整体打包（279 个文件 / 95 MB）
Source: "..\ScreenTime-WinUI\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只清理安装目录里安装器自己产生的东西；用户数据在 [Code] 里按勾选处理
Type: filesandordirs; Name: "{app}"

[Code]

var
  DeleteUserData: Boolean;
  DeleteDataCheckBox: TNewCheckBox;

{ 检查是否装了 .NET 8 运行时（Microsoft.NETCore.App 8.x） }
function HasDotNet8(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.NETCore.App\8.*'), FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if HasDotNet8() then Exit;

  // 静默安装时不弹窗（否则会卡住无人值守安装），直接失败
  if not WizardSilent() then
  begin
    if MsgBox('未检测到 .NET 8 运行时（x64）。' + #13#10#13#10 +
              'ScreenTime 需要它才能启动。是否现在打开官方下载页面？',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '',
                '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
  end;
  Result := False;
end;

{ 卸载前弹窗说明，并用复选框决定是否连使用数据一起删 }
function InitializeUninstall(): Boolean;
var
  Form: TSetupForm;
  Info: TNewStaticText;
  OkBtn, CancelBtn: TNewButton;
  DataDir: String;
begin
  Result := True;
  DeleteUserData := False;

  // 静默卸载不问，默认保留使用数据
  if UninstallSilent() then Exit;

  DataDir := ExpandConstant('{userappdata}\ScreenTime');

  Form := CreateCustomForm(ScaleX(430), ScaleY(210), True, False);
  try
    Form.Caption := '卸载 ScreenTime';

    Info := TNewStaticText.Create(Form);
    Info.Parent := Form;
    Info.Left := ScaleX(16);
    Info.Top := ScaleY(14);
    Info.Width := ScaleX(398);
    Info.Height := ScaleY(118);
    Info.WordWrap := True;
    Info.Caption :=
      '将从本机移除 ScreenTime 程序文件。' + #13#10#13#10 +
      '使用数据（数据库）默认保留在：' + #13#10 +
      DataDir + #13#10 +
      '重新安装后可继续查看历史记录。' + #13#10#13#10 +
      '提示：卸载前请先在托盘图标上右键退出 ScreenTime，否则文件被占用会卸载失败。';

    DeleteDataCheckBox := TNewCheckBox.Create(Form);
    DeleteDataCheckBox.Parent := Form;
    DeleteDataCheckBox.Left := ScaleX(16);
    DeleteDataCheckBox.Top := ScaleY(140);
    DeleteDataCheckBox.Width := ScaleX(398);
    DeleteDataCheckBox.Caption := '同时删除使用数据（不可恢复）';
    DeleteDataCheckBox.Checked := False;

    OkBtn := TNewButton.Create(Form);
    OkBtn.Parent := Form;
    OkBtn.Caption := '继续卸载';
    OkBtn.ModalResult := mrOk;
    OkBtn.Left := ScaleX(232);
    OkBtn.Top := ScaleY(172);
    OkBtn.Width := ScaleX(88);
    OkBtn.Height := ScaleY(26);
    OkBtn.Default := True;

    CancelBtn := TNewButton.Create(Form);
    CancelBtn.Parent := Form;
    CancelBtn.Caption := '取消';
    CancelBtn.ModalResult := mrCancel;
    CancelBtn.Left := ScaleX(326);
    CancelBtn.Top := ScaleY(172);
    CancelBtn.Width := ScaleX(88);
    CancelBtn.Height := ScaleY(26);
    CancelBtn.Cancel := True;

    { 用户点「取消」就中止卸载 }
    if Form.ShowModal() <> mrOk then
    begin
      Result := False;
      Exit;
    end;

    DeleteUserData := DeleteDataCheckBox.Checked;
  finally
    Form.Free();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and DeleteUserData then
    DelTree(ExpandConstant('{userappdata}\ScreenTime'), True, True, True);
end;
