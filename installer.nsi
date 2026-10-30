!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"

Unicode true

; General Settings
Name "AMURKA PVE Launcher"
OutFile "dist\AMURKA-PVE-Launcher-Setup-2.1.23.exe"
InstallDir "$LOCALAPPDATA\Programs\AMURKA PVE Launcher"
InstallDirRegKey HKCU "Software\AMURKA PVE Launcher" "InstallLocation"
RequestExecutionLevel user

; Icons
!define MUI_ICON "assets\icon.ico"
!define MUI_UNICON "assets\icon.ico"

; UI Settings
!define MUI_ABORTWARNING
!define MUI_HEADERIMAGE

; Pages
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES

; Finish Page
!define MUI_FINISHPAGE_RUN "$INSTDIR\AMURKA PVE Launcher.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Запустить AMURKA PVE Launcher"
!insertmacro MUI_PAGE_FINISH

; Uninstaller Pages
!insertmacro MUI_UNPAGE_WELCOME
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

; Language
!insertmacro MUI_LANGUAGE "Russian"

; Version Information
VIProductVersion "2.1.23.0"
VIAddVersionKey /LANG=${LANG_RUSSIAN} "ProductName" "AMURKA PVE Launcher"
VIAddVersionKey /LANG=${LANG_RUSSIAN} "CompanyName" "AMURKA PVE"
VIAddVersionKey /LANG=${LANG_RUSSIAN} "LegalCopyright" "DOMO"
VIAddVersionKey /LANG=${LANG_RUSSIAN} "FileDescription" "AMURKA PVE SCUM Mod Launcher"
VIAddVersionKey /LANG=${LANG_RUSSIAN} "FileVersion" "2.1.23.0"
VIAddVersionKey /LANG=${LANG_RUSSIAN} "ProductVersion" "2.1.23.0"

Section "MainSection" SecMain
    SetOutPath "$INSTDIR"

    ; Копируем файлы лаунчера и нативные библиотеки WPF (DirectX/DirectWrite/VCRuntime)
    File "AmurkaLauncher\publish-standalone\AMURKA PVE Launcher.exe"
    File /nonfatal "AmurkaLauncher\publish-standalone\*.dll"

    ; Создаем деинсталлятор
    WriteUninstaller "$INSTDIR\uninstall.exe"

    ; Запоминаем путь установки
    WriteRegStr HKCU "Software\AMURKA PVE Launcher" "InstallLocation" "$INSTDIR"

    ; Создаем ярлык на Рабочем столе
    CreateShortcut "$DESKTOP\AMURKA PVE Launcher.lnk" "$INSTDIR\AMURKA PVE Launcher.exe" "" "$INSTDIR\AMURKA PVE Launcher.exe" 0

    ; Создаем ярлыки в меню Пуск
    CreateDirectory "$SMPROGRAMS\AMURKA PVE Launcher"
    CreateShortcut "$SMPROGRAMS\AMURKA PVE Launcher\AMURKA PVE Launcher.lnk" "$INSTDIR\AMURKA PVE Launcher.exe" "" "$INSTDIR\AMURKA PVE Launcher.exe" 0
    CreateShortcut "$SMPROGRAMS\AMURKA PVE Launcher\Удалить AMURKA PVE Launcher.lnk" "$INSTDIR\uninstall.exe" "" "$INSTDIR\uninstall.exe" 0

    ; Регистрация в "Установка и удаление программ"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AMURKA-PVE-Launcher" "DisplayName" "AMURKA PVE Launcher"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AMURKA-PVE-Launcher" "DisplayIcon" "$INSTDIR\AMURKA PVE Launcher.exe,0"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AMURKA-PVE-Launcher" "DisplayVersion" "2.1.23"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AMURKA-PVE-Launcher" "Publisher" "AMURKA PVE"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AMURKA-PVE-Launcher" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AMURKA-PVE-Launcher" "UninstallString" '"$INSTDIR\uninstall.exe"'
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AMURKA-PVE-Launcher" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AMURKA-PVE-Launcher" "NoModify" 1
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AMURKA-PVE-Launcher" "NoRepair" 1

    ; Размер установленной программы
    ${GetSize} "$INSTDIR" "/S=K" $0 $1 $2
    IntFmt $0 "0x%08X" $0
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AMURKA-PVE-Launcher" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
    ; Удаляем ярлык с Рабочего стола
    Delete "$DESKTOP\AMURKA PVE Launcher.lnk"

    ; Удаляем ярлыки и папку в меню Пуск
    Delete "$SMPROGRAMS\AMURKA PVE Launcher\AMURKA PVE Launcher.lnk"
    Delete "$SMPROGRAMS\AMURKA PVE Launcher\Удалить AMURKA PVE Launcher.lnk"
    RMDir "$SMPROGRAMS\AMURKA PVE Launcher"

    ; Удаляем файлы программы
    Delete "$INSTDIR\AMURKA PVE Launcher.exe"
    Delete "$INSTDIR\*.dll"
    Delete "$INSTDIR\uninstall.exe"
    RMDir "$INSTDIR"

    ; Удаляем ключи из реестра
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AMURKA-PVE-Launcher"
    DeleteRegKey HKCU "Software\AMURKA PVE Launcher"
SectionEnd
