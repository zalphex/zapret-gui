Unicode True
RequestExecutionLevel admin
Name "zapretgui"
!define VER "2.1.1"
!define KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\ZapretGUI"
!define CORE "$PROGRAMFILES64\zapretgui\Desktop"
OutFile "..\bin\zapretgui-2.1.1-setup-x64.exe"
InstallDir "${CORE}"
SetCompressor /SOLID lzma
VIProductVersion "2.1.1.0"
VIAddVersionKey /LANG=1049 "ProductName" "zapretgui"
VIAddVersionKey /LANG=1049 "FileDescription" "Установщик zapretgui"
VIAddVersionKey /LANG=1049 "FileVersion" "${VER}"
VIAddVersionKey /LANG=1049 "ProductVersion" "${VER}"
VIAddVersionKey /LANG=1049 "LegalCopyright" "zalphex"
!include MUI2.nsh
!include x64.nsh
!include WinVer.nsh
!define MUI_ICON "..\Assets\zapretgui.ico"
!define MUI_UNICON "..\Assets\zapretgui.ico"
!define MUI_FINISHPAGE_RUN "${CORE}\zapretgui.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Открыть zapretgui"
!define MUI_DIRECTORYPAGE_TEXT_TOP "Выбери папку приложения. Утилиты, данные и копия для автозапуска хранятся в Program Files\zapretgui."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"
Var Previous
!macro CheckRunning
  System::Call 'kernel32::OpenMutexW(i 0x100000,i 0,w "Local\FlowPanel.Flowseal.Manager")p.r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    IfSilent +2
    MessageBox MB_ICONINFORMATION|MB_OK "Перед установкой или удалением выбери «Выход» в меню zapretgui в трее."
    SetErrorLevel 1618
    Abort
  ${EndIf}
!macroend
Function .onInit
  ${IfNot} ${IsNativeAMD64}
    MessageBox MB_OK "Для этой сборки нужна Windows x64 на Intel или AMD."
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK "Требуется Windows 10 или 11."
    Abort
  ${EndIf}
  SetRegView 64
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 < 528040
    MessageBox MB_ICONSTOP|MB_OK "Установи Microsoft .NET Framework 4.8 или 4.8.1 и запусти установщик снова."
    SetErrorLevel 1633
    Abort
  ${EndIf}
  !insertmacro CheckRunning
  ReadRegStr $Previous HKLM "${KEY}" "InstallLocation"
  ${If} $Previous != ""
  ${AndIf} $Previous != "$PROGRAMFILES64\FlowPanel\Desktop"
    StrCpy $INSTDIR "$Previous"
  ${EndIf}
FunctionEnd
Function un.onInit
  !insertmacro CheckRunning
FunctionEnd
Section
  SetRegView 64
  SetShellVarContext all
  SetOverwrite on
  SetOutPath "${CORE}"
  File "/oname=zapretgui.exe" "..\bin\zapretgui.exe"
  File "/oname=zapretgui-tray.exe" "..\bin\zapretgui.exe"
  File "/oname=update-worker-${VER}.exe" "..\bin\zapretgui.exe"
  WriteUninstaller "${CORE}\Uninstall.exe"
  StrCpy $0 -1
  ExecWait '"${CORE}\zapretgui.exe" --setup migrate' $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "Не удалось перенести настройки предыдущей установки."
    SetErrorLevel 1
    Abort
  ${EndIf}
  SetOutPath "$INSTDIR"
  ${If} "$INSTDIR" != "${CORE}"
    File "/oname=zapretgui.exe" "..\bin\zapretgui.exe"
  ${EndIf}
  CreateDirectory "$SMPROGRAMS\zapretgui"
  CreateShortcut "$SMPROGRAMS\zapretgui\zapretgui.lnk" "$INSTDIR\zapretgui.exe" "" "$INSTDIR\zapretgui.exe"
  SetOutPath "${CORE}"
  ExecWait '"${CORE}\zapretgui.exe" --setup shortcut "$SMPROGRAMS\zapretgui\zapretgui.lnk"'
  WriteRegStr HKLM "${KEY}" "DisplayName" "zapretgui"
  WriteRegStr HKLM "${KEY}" "DisplayVersion" "${VER}"
  WriteRegStr HKLM "${KEY}" "Publisher" "zalphex"
  WriteRegStr HKLM "${KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${KEY}" "DisplayIcon" "$INSTDIR\zapretgui.exe"
  WriteRegStr HKLM "${KEY}" "UninstallString" '"${CORE}\Uninstall.exe"'
  WriteRegDWORD HKLM "${KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${KEY}" "NoRepair" 1
  ; Retire scripts owned by previous zapretgui versions in the managed directory.
  Delete "${CORE}\FlowPanel.ps1"
  Delete "${CORE}\Settings.ps1"
  Delete "${CORE}\PanelWorker.ps1"
  Delete "${CORE}\AutoStart.ps1"
  Delete "${CORE}\AppUpdate.ps1"
  Delete "${CORE}\Engine.ps1"
  Delete "${CORE}\Migrate.ps1"
  Delete "${CORE}\RegisterShortcut.ps1"
  Delete "${CORE}\Uninstall.ps1"
SectionEnd
Section Uninstall
  SetRegView 64
  SetShellVarContext all
  ReadRegStr $Previous HKLM "${KEY}" "InstallLocation"
  StrCpy $0 -1
  ExecWait '"${CORE}\zapretgui.exe" --setup uninstall' $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "Не удалось остановить компоненты. Удаление прервано."
    SetErrorLevel 1
    Abort
  ${EndIf}
  Delete "$SMPROGRAMS\zapretgui\zapretgui.lnk"
  RMDir "$SMPROGRAMS\zapretgui"
  ${If} $Previous != ""
  ${AndIf} $Previous != "${CORE}"
    Delete "$Previous\zapretgui.exe"
    RMDir "$Previous"
  ${EndIf}
  Delete "${CORE}\zapretgui.exe"
  Delete "${CORE}\zapretgui-tray.exe"
  Delete "${CORE}\update-worker-*.exe"
  Delete "${CORE}\Uninstall.exe"
  RMDir "${CORE}"
  DeleteRegKey HKLM "${KEY}"
SectionEnd
