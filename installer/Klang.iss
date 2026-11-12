; Klang (WinUI 3) 安装程序脚本，用 Inno Setup 6 编译。
; 命令行：ISCC.exe /DAppVersion=2.4.0 /DSourceDir=..\publish\Klang.WinUI installer\Klang.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\Klang.WinUI"
#endif

[Setup]
AppId={{B6D0C6B4-6B8E-4E7B-9C0F-3A5D2F6E41A7}
AppName=Klang
AppVersion={#AppVersion}
AppVerName=Klang {#AppVersion}
AppPublisher=gwj0323
AppPublisherURL=https://github.com/gwj0323/klang
AppSupportURL=https://github.com/gwj0323/klang/issues
AppUpdatesURL=https://github.com/gwj0323/klang/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\Klang
DefaultGroupName=Klang
DisableProgramGroupPage=yes
; 默认只为当前用户安装（不需要管理员权限）；安装时可以选择为所有用户安装
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist-installer
OutputBaseFilename=Klang-Setup-{#AppVersion}-x64
SetupIconFile=..\Klang.WinUI\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Klang.WinUI.exe
UninstallDisplayName=Klang
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
#if FileExists(CompilerPath + "Languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Klang"; Filename: "{app}\Klang.WinUI.exe"
Name: "{autodesktop}\Klang"; Filename: "{app}\Klang.WinUI.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Klang.WinUI.exe"; Description: "{cm:LaunchProgram,Klang}"; Flags: nowait postinstall skipifsilent
