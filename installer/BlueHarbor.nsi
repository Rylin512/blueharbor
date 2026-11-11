; BlueHarbor installer (NSIS 3.x, per-user, no administrator required)
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"

!define APP_NAME "BlueHarbor"
!define APP_VERSION "1.0.6"
!define APP_PUBLISHER "BlueHarbor"
!define APP_EXE "BlueHarbor.exe"
!define APP_ID "BlueHarbor"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"

Name "${APP_NAME} ${APP_VERSION}"
OutFile "..\dist\BlueHarbor-Setup-${APP_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\BlueHarbor"
InstallDirRegKey HKCU "Software\${APP_ID}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64
SetOverwrite on
BrandingText "${APP_NAME} · 蓝牙音频接收器"

VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey "FileDescription" "BlueHarbor 蓝牙音频接收器安装程序"
VIAddVersionKey "FileVersion" "${APP_VERSION}.0"
VIAddVersionKey "LegalCopyright" "Copyright © 2026 BlueHarbor"

!define MUI_ICON "..\src\BlueHarbor\Assets\BlueHarbor.ico"
!define MUI_UNICON "..\src\BlueHarbor\Assets\BlueHarbor.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "安装 BlueHarbor"
!define MUI_WELCOMEPAGE_TEXT "BlueHarbor 让这台 Windows 电脑成为稳定的蓝牙音频接收器。$\r$\n$\r$\n安装到当前用户目录，不需要管理员权限。手机端无需安装 App，使用系统蓝牙即可。"
!define MUI_FINISHPAGE_TITLE "BlueHarbor 已安装"
!define MUI_FINISHPAGE_TEXT "下一步：$\r$\n1. 在 Windows 设置中配对手机。$\r$\n2. 打开 BlueHarbor，选择手机并点击“开启接收”。$\r$\n3. 在手机上选择这台电脑作为蓝牙音频输出。$\r$\n$\r$\n关闭窗口后程序会留在系统托盘继续工作。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即启动 BlueHarbor"
!define MUI_FINISHPAGE_NOREBOOTSUPPORT

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  SetShellVarContext current
FunctionEnd

Function un.onInit
  SetShellVarContext current
FunctionEnd

Section "BlueHarbor" SecMain
  SectionIn RO
  SetShellVarContext current
  SetOutPath "$INSTDIR"

  ; Stop a running copy so the executable can be replaced.
  nsExec::ExecToLog 'taskkill /IM ${APP_EXE} /F'
  Sleep 1500

  ; Folder-based payload: no single-file extraction, much faster startup.
  File /r "..\dist\app\*.*"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\BlueHarbor"
  CreateShortcut "$SMPROGRAMS\BlueHarbor\BlueHarbor.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortcut "$SMPROGRAMS\BlueHarbor\卸载 BlueHarbor.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\BlueHarbor.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0

  WriteRegStr HKCU "Software\${APP_ID}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP_NAME} 蓝牙音频接收器"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE},0"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" 190000
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  nsExec::ExecToLog 'taskkill /IM ${APP_EXE} /F'
  Sleep 1000

  RMDir /r "$INSTDIR"

  Delete "$SMPROGRAMS\BlueHarbor\BlueHarbor.lnk"
  Delete "$SMPROGRAMS\BlueHarbor\卸载 BlueHarbor.lnk"
  RMDir "$SMPROGRAMS\BlueHarbor"
  Delete "$DESKTOP\BlueHarbor.lnk"

  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${APP_ID}"
  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "Software\${APP_ID}"

  ; Deliberately keep %LOCALAPPDATA%\BlueHarbor (logs and settings) so that
  ; reinstalling preserves the paired-device selection.
SectionEnd
