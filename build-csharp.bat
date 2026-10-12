@echo off
title Сборка AMURKA PVE Launcher (C#)
cd /d "%~dp0AmurkaLauncher"
echo Сборка C# проекта в конфигурации Release...
dotnet build -c Release
if %ERRORLEVEL% EQU 0 (
    echo.
    echo ====================================
    echo   Сборка успешно завершена!
    echo ====================================
) else (
    echo.
    echo Ошибка при сборке.
)
pause
