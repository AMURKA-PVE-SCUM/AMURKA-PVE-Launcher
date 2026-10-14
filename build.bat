@echo off
title Сборка AMURKA PVE Launcher
cd /d "%~dp0"

echo ===================================================
echo   Сборка AMURKA PVE Launcher (.NET 8 WPF)
echo ===================================================
echo.

dotnet build AmurkaLauncher.sln -c Release

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ===================================================
    echo   Сборка успешно завершена!
    echo   Исполняемый файл: AmurkaLauncher\bin\Release\net8.0-windows\AMURKA PVE Launcher.exe
    echo ===================================================
) else (
    echo.
    echo [ОШИБКА] Сборка завершилась с ошибкой (%ERRORLEVEL%).
)
echo.
pause
