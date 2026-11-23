Unicode True
RequestExecutionLevel admin
Name "zapretgui"
!define VER "2.1.5"
!define KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\ZapretGUI"
!define CORE "$INSTDIR\zapretgui.runtime"
OutFile "..\bin\zapretgui-2.1.5-setup-x64.exe"
InstallDir "$PROGRAMFILES64\zapretgui"
SetCompressor /SOLID lzma
VIProductVersion "2.1.5.0"
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
!define MUI_DIRECTORYPAGE_TEXT_TOP "Выбери папку приложения. Программа, конфиги zapret и TG WS Proxy будут храниться здесь, в подпапке zapretgui.runtime."
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
  SetRegView 64
  ReadRegStr $0 HKLM "${KEY}" "RuntimeRoot"
  ${If} $0 != "$INSTDIR"
    MessageBox MB_OK "Действующая установка находится в другой папке."
    Abort
  ${EndIf}
  !insertmacro CheckRunning
FunctionEnd
Section
  SetRegView 64
  SetShellVarContext all
  SetOverwrite on
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File "/oname=prepare.exe" "..\bin\zapretgui.exe"
  StrCpy $0 -1
  ExecWait '"$PLUGINSDIR\prepare.exe" --setup prepare "${CORE}"' $0
  ${If} $0 != 0
    SetErrorLevel 1
    Abort
  ${EndIf}
  SetOutPath "${CORE}"
  File "/oname=zapretgui.exe" "..\bin\zapretgui.exe"
  File "/oname=zapretgui-tray.exe" "..\bin\zapretgui.exe"
  WriteUninstaller "${CORE}\Uninstall.exe"
  StrCpy $0 -1
  ExecWait '"${CORE}\zapretgui.exe" --setup startup-path' $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "Не удалось обновить путь автозапуска."
    SetErrorLevel 1
    Abort
  ${EndIf}
  CreateDirectory "$SMPROGRAMS\zapretgui"
  CreateShortcut "$SMPROGRAMS\zapretgui\zapretgui.lnk" "${CORE}\zapretgui.exe" "" "${CORE}\zapretgui.exe"
  ExecWait '"${CORE}\zapretgui.exe" --setup shortcut "$SMPROGRAMS\zapretgui\zapretgui.lnk"'
  WriteRegStr HKLM "${KEY}" "DisplayName" "zapretgui"
  WriteRegStr HKLM "${KEY}" "DisplayVersion" "${VER}"
  WriteRegStr HKLM "${KEY}" "Publisher" "zalphex"
  WriteRegStr HKLM "${KEY}" "RuntimeRoot" "${CORE}"
  WriteRegStr HKLM "${KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${KEY}" "DisplayIcon" "${CORE}\zapretgui.exe"
  WriteRegStr HKLM "${KEY}" "UninstallString" '"${CORE}\Uninstall.exe"'
  WriteRegDWORD HKLM "${KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${KEY}" "NoRepair" 1
SectionEnd
Section Uninstall
  SetRegView 64
  SetShellVarContext all
  ReadRegStr $Previous HKLM "${KEY}" "InstallLocation"
  StrCpy $0 -1
  ExecWait '"$INSTDIR\zapretgui.exe" --setup uninstall' $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "Не удалось остановить компоненты. Удаление прервано."
    SetErrorLevel 1
    Abort
  ${EndIf}
  Delete "$SMPROGRAMS\zapretgui\zapretgui.lnk"
  RMDir "$SMPROGRAMS\zapretgui"
  Delete "$INSTDIR\zapretgui.exe"
  Delete "$INSTDIR\zapretgui-tray.exe"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKLM "${KEY}"
SectionEnd
