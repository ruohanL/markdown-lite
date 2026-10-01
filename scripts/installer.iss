; MarkdownLite 安装程序脚本（Inno Setup 6）
;
; 产物形态：per-user 免管理员安装（与 associate-md.ps1 的 HKCU 关联理念一致），
;   默认安装到 %LOCALAPPDATA%\Programs\MarkdownLite，开始菜单快捷方式，
;   可选桌面快捷方式与 .md 文件关联（复用 scripts\associate-md.ps1 的注册逻辑）。
;
; 构建：
;   iscc /DAppVersion=1.0.0 scripts\installer.iss
;   AppVersion 由调用方从 MarkdownLite.csproj 的 <Version> 读取传入（见 scripts/release.ps1）；
;   不传时回退 1.0.0。发布产物与便携版一起作为 GitHub Release 资产。

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "MarkdownLite"
#define AppExe "MarkdownLite.exe"

[Setup]
AppId={{C7442A14-4F10-44B6-B84B-BDEDB282AB65}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName} Contributors
; 免管理员：整台安装只写 HKCU 与用户目录
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; 卸载时保留用户设置/阅读进度（%LOCALAPPDATA%\MdReader、%APPDATA%\MdReader）
UninstallFilesDir={app}\uninst
LicenseFile=..\LICENSE
OutputDir=..\release
OutputBaseFilename=MarkdownLite-Setup-{#AppVersion}
SetupIconFile=..\src\MarkdownLite\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chineseSimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; 安装器装「目录形态」发布（非单文件）：文件直接铺到安装目录，运行时没有自解压环节，
; 首次启动明显快于单文件便携版（单文件首次要解压约 68MB 并被安全软件全量首扫）。
; 内容源由 scripts/release.ps1 以 R2R + 自包含参数预先产出；pdb 不随产品分发。
Source: "..\release\installer-src\*"; DestDir: "{app}"; \
  Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
; 文件类型图标（.md 在资源管理器里显示的白色文档卡片），供 associate-md.ps1 -IconSource 使用
Source: "..\src\MarkdownLite\md-file.ico"; DestDir: "{app}"; Flags: ignoreversion
; 关联注册脚本：与命令行手工注册是同一份逻辑，安装器只是另一种触发方式
Source: "..\scripts\associate-md.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "mdassoc"; Description: "注册 .md/.markdown 文件关联（加入「打开方式」列表）"; GroupDescription: "文件关联："; Flags: checkedonce
Name: "mdassoc\default"; Description: "将 MarkdownLite 设为 .md 默认打开程序（原默认程序会被记录，卸载时还原）"; Flags: unchecked

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; 安装完成后按所选任务注册 .md 关联（HKCU，无需管理员；无提示音、无弹窗，失败不影响安装）
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\associate-md.ps1"" -ExePath ""{app}\{#AppExe}"" -IconSource ""{app}\md-file.ico"" -SetDefault"; \
  Tasks: mdassoc\default; Flags: runhidden
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\associate-md.ps1"" -ExePath ""{app}\{#AppExe}"" -IconSource ""{app}\md-file.ico"""; \
  Tasks: mdassoc; Flags: runhidden
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载时清理 .md 关联（含把默认程序还原为注册前的那一个）
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\associate-md.ps1"" -Unregister"; \
  Flags: runhidden; RunOnceId: "UnassocMd"
