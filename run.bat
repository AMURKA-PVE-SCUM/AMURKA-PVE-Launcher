@echo off
title AMURKA PVE Launcher (C#)
cd /d "%~dp0"
echo ====================================
echo   AMURKA PVE MOD LAUNCHER
echo   Created by DOMO
echo ====================================
echo.

if exist "AmurkaLauncher\bin\Release\net8.0-windows\AMURKA PVE Launcher.exe" (
    echo Запуск C# лаунчера...
    start "" "AmurkaLauncher\bin\Release\net8.0-windows\AMURKA PVE Launcher.exe"
) else (
    echo Запуск Electron лаунчера...
    start /wait npx electron .
)
