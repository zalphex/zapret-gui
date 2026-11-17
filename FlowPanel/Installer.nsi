Unicode True
RequestExecutionLevel admin
Name "zapretgui"
OutFile "../zapret_GUI_Setup_x64.exe"
InstallDir "$PROGRAMFILES64\zapretgui\Desktop"
SetCompressor /SOLID lzma
VIProductVersion "1.3.18.0"
VIAddVersionKey /LANG=1049 "ProductName" "zapretgui"
VIAddVersionKey /LANG=1049 "FileDescription" "Установщик zapretgui"
VIAddVersionKey /LANG=1049 "FileVersion" "1.3.18"

!include "MUI2.nsh"
!include "x64.nsh"
!define UNKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\ZapretGUI"
!define MUI_ICON "zapretgui.ico"
!define MUI_UNICON "zapretgui.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\zapretgui.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Открыть zapretgui"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"

!macro CheckForRunning
  System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Local\FlowPanel.Flowseal.Manager") p .r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONEXCLAMATION "Закрой zapretgui. Если окно свёрнуто, выбери «Выход» в трее."
    Abort
  ${EndIf}
!macroend

Function .onInit
  ${IfNot} ${IsNativeAMD64}
    MessageBox MB_OK|MB_ICONSTOP "Требуется Windows 10 или 11 x64 (Intel или AMD)."
    Abort
  ${EndIf}
  !insertmacro CheckForRunning
  SetRegView 64
  ReadRegStr $0 HKLM "${UNKEY}" "InstallLocation"
  ${If} $0 != ""
  ${AndIf} $0 != "$PROGRAMFILES64\FlowPanel\Desktop"
    StrCpy $INSTDIR "$0"
  ${EndIf}
FunctionEnd

Function un.onInit
  !insertmacro CheckForRunning
FunctionEnd

!macro RemoveLegacyScripts Directory
  Delete "${Directory}\FlowPanel.ps1"
  Delete "${Directory}\Settings.ps1"
  Delete "${Directory}\PanelWorker.ps1"
  Delete "${Directory}\AutoStart.ps1"
  Delete "${Directory}\AppUpdate.ps1"
  Delete "${Directory}\Engine.ps1"
  Delete "${Directory}\Migrate.ps1"
  Delete "${Directory}\RegisterShortcut.ps1"
  Delete "${Directory}\Uninstall.ps1"
!macroend

Section "zapretgui"
  SetShellVarContext all
  SetRegView 64
  SetOverwrite on

  ; The scheduled task and uninstaller always run from Program Files.
  SetOutPath "$PROGRAMFILES64\zapretgui\Desktop"
  File /oname=zapretgui.exe "zapretgui.exe"
  File /oname=zapretgui-tray.exe "zapretgui.exe"
  WriteUninstaller "$PROGRAMFILES64\zapretgui\Desktop\Uninstall.exe"
  StrCpy $0 -1
  ExecWait '"$OUTDIR\zapretgui.exe" --setup migrate' $0
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "Не удалось перенести предыдущую установку. Старые списки и конфиги сохранены."
    Abort
  ${EndIf}

  SetOutPath "$INSTDIR"
  ${If} "$INSTDIR" != "$PROGRAMFILES64\zapretgui\Desktop"
    File /oname=zapretgui.exe "zapretgui.exe"
  ${EndIf}
  File "README.txt"
  Delete "$SMPROGRAMS\zapret GUI\zapret GUI.lnk"
  RMDir "$SMPROGRAMS\zapret GUI"
  CreateDirectory "$SMPROGRAMS\zapretgui"
  CreateShortcut "$SMPROGRAMS\zapretgui\zapretgui.lnk" "$INSTDIR\zapretgui.exe" "" "$INSTDIR\zapretgui.exe" 0
  StrCpy $0 -1
  ExecWait '"$PROGRAMFILES64\zapretgui\Desktop\zapretgui.exe" --setup shortcut "$SMPROGRAMS\zapretgui\zapretgui.lnk"' $0
  ${If} $0 != 0
    DetailPrint "Не удалось задать имя приложения для панели задач."
  ${EndIf}
  WriteRegStr HKLM "${UNKEY}" "DisplayName" "zapretgui"
  WriteRegStr HKLM "${UNKEY}" "DisplayVersion" "1.3.18"
  WriteRegStr HKLM "${UNKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNKEY}" "DisplayIcon" "$INSTDIR\zapretgui.exe"
  WriteRegStr HKLM "${UNKEY}" "UninstallString" '$\"$PROGRAMFILES64\zapretgui\Desktop\Uninstall.exe$\"'
  WriteRegDWORD HKLM "${UNKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNKEY}" "NoRepair" 1
  !insertmacro RemoveLegacyScripts "$PROGRAMFILES64\zapretgui\Desktop"
  ${If} "$INSTDIR" != "$PROGRAMFILES64\zapretgui\Desktop"
    !insertmacro RemoveLegacyScripts "$INSTDIR"
  ${EndIf}
SectionEnd

!macro RemoveAppFiles Directory
  Delete "${Directory}\zapretgui.exe"
  Delete "${Directory}\zapretgui-tray.exe"
  !insertmacro RemoveLegacyScripts "${Directory}"
  Delete "${Directory}\TaskbarIdentity.cs"
  Delete "${Directory}\Panel.xaml"
  Delete "${Directory}\Probe.cs"
  Delete "${Directory}\README.txt"
  RMDir "${Directory}"
!macroend

Section "Uninstall"
  SetShellVarContext all
  SetRegView 64
  StrCpy $1 "$PROGRAMFILES64\zapretgui\Desktop"
  ReadRegStr $2 HKLM "${UNKEY}" "InstallLocation"
  ${If} $2 == ""
    StrCpy $2 "$1"
  ${EndIf}
  StrCpy $0 -1
  ExecWait '"$1\zapretgui.exe" --setup uninstall' $0
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "Не удалось отключить автозапуск. Повтори удаление позже."
    Abort
  ${EndIf}
  Delete "$SMPROGRAMS\zapretgui\zapretgui.lnk"
  RMDir "$SMPROGRAMS\zapretgui"
  DeleteRegKey HKLM "${UNKEY}"
  !insertmacro RemoveAppFiles "$2"
  ${If} "$1" != "$2"
    !insertmacro RemoveAppFiles "$1"
  ${EndIf}
  Delete "$1\Uninstall.exe"
  RMDir "$1"
SectionEnd
