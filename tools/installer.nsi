Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"
!include "StrFunc.nsh"
${UnStrStr}
Name "Dynamic Glow"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\Dynamic Glow"
InstallDirRegKey HKCU "Software\DynamicGlow\Installer" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "Dynamic Glow"
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "Dynamic Glow"
VIAddVersionKey /LANG=1033 "FileDescription" "Dynamic Glow Installer"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright 2026 Deskland authors and Dynamic Glow contributors"
!define MUI_ABORTWARNING
!define MUI_ICON "${PAYLOAD}\host\bin\DynamicGlow.ico"
!define MUI_UNICON "${PAYLOAD}\host\bin\DynamicGlow.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\host\bin\DynamicGlow.exe"
!define MUI_FINISHPAGE_RUN_TEXT "$(LaunchApp)"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${PAYLOAD}\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "English"
LangString LaunchApp ${LANG_ENGLISH} "Launch Dynamic Glow"
LangString AppFiles ${LANG_ENGLISH} "Dynamic Glow (your own wallpaper)"
LangString DesktopShortcut ${LANG_ENGLISH} "Desktop shortcut"
LangString NeedX64 ${LANG_ENGLISH} "64-bit Windows 10 or later is required."
LangString CloseFailed ${LANG_ENGLISH} "Please exit Dynamic Glow and retry."
LangString RuntimeFailed ${LANG_ENGLISH} "WebView2 setup did not complete. Connect to the internet, install Microsoft Edge WebView2 Runtime, and retry."
LangString InstallingRuntime ${LANG_ENGLISH} "Installing Microsoft Edge WebView2 Runtime (internet required)..."

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "$(NeedX64)"
    SetErrorLevel 1
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "$(NeedX64)"
    SetErrorLevel 1
    Abort
  ${EndIf}
  SetShellVarContext current
  SetRegView 64
  !insertmacro MUI_LANGDLL_DISPLAY
FunctionEnd

Function CheckRuntime
  StrCpy $0 ""
  SetRegView 32
  ReadRegStr $0 HKLM "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${If} $0 == ""
    ReadRegStr $0 HKCU "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${EndIf}
  SetRegView 64
  ${If} $0 == ""
    ReadRegStr $0 HKCU "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${EndIf}
FunctionEnd

Section "$(AppFiles)" SecApp
  SectionIn RO
  Call CheckRuntime
  ${If} $0 == ""
  ${OrIf} $0 == "0.0.0.0"
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File /oname=MicrosoftEdgeWebview2Setup.exe "${BOOTSTRAPPER}"
    DetailPrint "$(InstallingRuntime)"
    ExecWait '"$PLUGINSDIR\MicrosoftEdgeWebview2Setup.exe" /silent /install' $1
    Call CheckRuntime
    ${If} $0 == ""
    ${OrIf} $0 == "0.0.0.0"
      MessageBox MB_ICONSTOP "$(RuntimeFailed)"
      SetErrorLevel 2
      Abort
    ${EndIf}
  ${EndIf}
  IfFileExists "$INSTDIR\host\bin\DynamicGlow.exe" 0 closed
  ExecWait '"$INSTDIR\host\bin\DynamicGlow.exe" --exit-installed' $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "$(CloseFailed)"
    SetErrorLevel 3
    Abort
  ${EndIf}
  closed:
  !include "${FILE_LIST}"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\Dynamic Glow"
  CreateShortcut "$SMPROGRAMS\Dynamic Glow\Dynamic Glow.lnk" "$INSTDIR\host\bin\DynamicGlow.exe"
  CreateShortcut "$SMPROGRAMS\Dynamic Glow\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\DynamicGlow\Installer" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicGlow" "DisplayName" "Dynamic Glow"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicGlow" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicGlow" "Publisher" "Dynamic Glow"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicGlow" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicGlow" "DisplayIcon" "$INSTDIR\host\bin\DynamicGlow.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicGlow" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicGlow" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicGlow" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicGlow" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicGlow" "EstimatedSize" $0
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
FunctionEnd
Section "Uninstall"
  IfFileExists "$INSTDIR\host\bin\DynamicGlow.exe" 0 unclosed
  ExecWait '"$INSTDIR\host\bin\DynamicGlow.exe" --exit-installed' $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "$(CloseFailed)"
    SetErrorLevel 3
    Abort
  ${EndIf}
  unclosed:
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "DynamicGlow"
  ${UnStrStr} $1 $0 '$\"$INSTDIR\host\bin\DynamicGlow.exe$\"'
  ${If} $1 != ""
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "DynamicGlow"
  ${EndIf}
  !include "${DELETE_LIST}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\Dynamic Glow\Dynamic Glow.lnk"
  Delete "$SMPROGRAMS\Dynamic Glow\Uninstall.lnk"
  RMDir "$SMPROGRAMS\Dynamic Glow"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicGlow"
  DeleteRegKey HKCU "Software\DynamicGlow\Installer"
SectionEnd
