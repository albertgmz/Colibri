Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
!ifndef PAYLOAD_PATH
  !error "PAYLOAD_PATH is required"
!endif
!ifndef INSTALLER_PATH
  !error "INSTALLER_PATH is required"
!endif
!ifndef PACKAGE_VERSION
  !error "PACKAGE_VERSION is required"
!endif
!ifndef HELPER_PATH
  !error "HELPER_PATH is required"
!endif

Name "Colibri"
VIProductVersion "${PACKAGE_VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "Colibri"
VIAddVersionKey /LANG=1033 "ProductVersion" "${PACKAGE_VERSION}"
VIAddVersionKey /LANG=1033 "FileVersion" "${PACKAGE_VERSION}.0"
VIAddVersionKey /LANG=1033 "FileDescription" "Colibri setup"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Colibri contributors"
OutFile "${INSTALLER_PATH}"
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\Programs\Colibri"
SetCompressor /SOLID lzma
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Colibri"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!macro Initialize PREFIX
Function ${PREFIX}.onInit
  SetShellVarContext current
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "This package requires 64-bit Windows."
    Abort
  ${EndIf}
  ; A fixed location keeps ownership and process checks consistent on install and uninstall.
  StrCpy $INSTDIR "$LOCALAPPDATA\Programs\Colibri"
FunctionEnd
!macroend
!insertmacro Initialize ""
!insertmacro Initialize "un"

!macro RunHelper ACTION EXTRA
  ClearErrors
  ExecWait '"$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\install-helper.ps1" -Action ${ACTION} ${EXTRA}' $0
  ${If} ${Errors}
  ${OrIf} $0 != 0
    MessageBox MB_ICONSTOP "Colibri setup stopped. Close installed Colibri and its download engine, and check that the installed files are writable. No processes will be stopped automatically.$\r$\n$\r$\nDetails: $TEMP\Colibri-setup.log"
    SetErrorLevel 1
    Abort
  ${EndIf}
!macroend

!macro RequireSuccess MESSAGE
  ${If} ${Errors}
    MessageBox MB_ICONSTOP "${MESSAGE}"
    SetErrorLevel 1
    Abort
  ${EndIf}
!macroend

Section "Colibri (required)" MainSection
  SectionIn RO
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  ClearErrors
  File /oname=install-helper.ps1 "${HELPER_PATH}"
  !insertmacro RequireSuccess "Could not extract the installation helper."
  SectionGetFlags 1 $1
  IntOp $1 $1 & 1
  ${If} $1 == 1
    !insertmacro RunHelper Check "-IncludeDesktop"
  ${Else}
    !insertmacro RunHelper Check ""
  ${EndIf}
  SetOutPath "$PLUGINSDIR\payload"
  ClearErrors
  File /r "${PAYLOAD_PATH}\*.*"
  !insertmacro RequireSuccess "Could not extract the application payload."
  !insertmacro RunHelper Install '-Payload "$PLUGINSDIR\payload"'
  ClearErrors
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  !insertmacro RequireSuccess "Could not write the uninstaller. Rerun setup to repair this installation."
  ${If} $1 == 1
    !insertmacro RunHelper Shortcuts "-IncludeDesktop"
  ${Else}
    !insertmacro RunHelper Shortcuts ""
  ${EndIf}
  ClearErrors
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "Colibri"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${PACKAGE_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Colibri"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Colibri.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  !insertmacro RequireSuccess "Could not write per-user uninstall metadata."
SectionEnd

Section /o "Desktop shortcut" DesktopSection
  ; The required section preflights and creates this option together with Start Menu shortcuts.
  DetailPrint "Desktop shortcut selected."
SectionEnd

Section "Uninstall"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  ClearErrors
  File /oname=install-helper.ps1 "${HELPER_PATH}"
  !insertmacro RequireSuccess "Could not extract the uninstall helper."
  !insertmacro RunHelper Remove ""
  !insertmacro RunHelper RemoveExternal ""
  ClearErrors
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  !insertmacro RequireSuccess "Could not remove per-user uninstall metadata."
  !insertmacro RunHelper FinalizeRemove ""
SectionEnd
