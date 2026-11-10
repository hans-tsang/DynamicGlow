Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"
!include "StrFunc.nsh"
${UnStrStr}
Name "Dynamic Island"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\Dynamic Island"
InstallDirRegKey HKCU "Software\DynamicIsland\Installer" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "Dynamic Island"
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "Dynamic Island"
VIAddVersionKey /LANG=1033 "FileDescription" "Dynamic Island Installer"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright 2026 Deskland authors and Dynamic Island contributors"
!define MUI_ABORTWARNING
!define MUI_ICON "${PAYLOAD}\host\bin\DynamicIsland.ico"
!define MUI_UNICON "${PAYLOAD}\host\bin\DynamicIsland.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\host\bin\DynamicIsland.exe"
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
LangString LaunchApp ${LANG_ENGLISH} "Launch Dynamic Island"
LangString AppFiles ${LANG_ENGLISH} "Dynamic Island (your own wallpaper)"
LangString DesktopShortcut ${LANG_ENGLISH} "Desktop shortcut"
LangString NeedX64 ${LANG_ENGLISH} "64-bit Windows 10 or later is required."
LangString CloseFailed ${LANG_ENGLISH} "Please exit Dynamic Island and retry."
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
  IfFileExists "$INSTDIR\host\bin\DynamicIsland.exe" 0 closed
  ExecWait '"$INSTDIR\host\bin\DynamicIsland.exe" --exit-installed' $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "$(CloseFailed)"
    SetErrorLevel 3
    Abort
  ${EndIf}
  closed:
  !include "${FILE_LIST}"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\Dynamic Island"
  CreateShortcut "$SMPROGRAMS\Dynamic Island\Dynamic Island.lnk" "$INSTDIR\host\bin\DynamicIsland.exe"
  CreateShortcut "$SMPROGRAMS\Dynamic Island\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\DynamicIsland\Installer" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicIsland" "DisplayName" "Dynamic Island"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicIsland" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicIsland" "Publisher" "Dynamic Island"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicIsland" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicIsland" "DisplayIcon" "$INSTDIR\host\bin\DynamicIsland.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicIsland" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicIsland" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicIsland" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicIsland" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicIsland" "EstimatedSize" $0
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
FunctionEnd
Section "Uninstall"
  IfFileExists "$INSTDIR\host\bin\DynamicIsland.exe" 0 unclosed
  ExecWait '"$INSTDIR\host\bin\DynamicIsland.exe" --exit-installed' $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "$(CloseFailed)"
    SetErrorLevel 3
    Abort
  ${EndIf}
  unclosed:
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "DynamicIsland"
  ${UnStrStr} $1 $0 '$\"$INSTDIR\host\bin\DynamicIsland.exe$\"'
  ${If} $1 != ""
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "DynamicIsland"
  ${EndIf}
  !include "${DELETE_LIST}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\Dynamic Island\Dynamic Island.lnk"
  Delete "$SMPROGRAMS\Dynamic Island\Uninstall.lnk"
  RMDir "$SMPROGRAMS\Dynamic Island"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DynamicIsland"
  DeleteRegKey HKCU "Software\DynamicIsland\Installer"
SectionEnd
