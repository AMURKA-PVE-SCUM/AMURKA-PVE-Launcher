@echo off
title AMURKA PVE Launcher
cd /d "%~dp0"

echo ===================================================
echo   AMURKA PVE MOD LAUNCHER
echo   Created by DOMO
echo ===================================================
echo.

set "EXE_PATH=AmurkaLauncher\bin\Release\net8.0-windows\AMURKA PVE Launcher.exe"
set "DEBUG_EXE_PATH=AmurkaLauncher\bin\Debug\net8.0-windows\AMURKA PVE Launcher.exe"

if exist "%EXE_PATH%" (
    echo Запуск лаунчера (Release)...
    start "" "%EXE_PATH%"
) else if exist "%DEBUG_EXE_PATH%" (
    echo Запуск лаунчера (Debug)...
    start "" "%DEBUG_EXE_PATH%"
) else (
    echo Скомпилированный лаунчер не найден. Запуск через dotnet run...
    dotnet run --project .\AmurkaLauncher\AmurkaLauncher.csproj -c Release
)
