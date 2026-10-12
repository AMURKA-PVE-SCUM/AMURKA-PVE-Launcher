@echo off
title Сборка установщика AMURKA PVE Launcher (C#)
cd /d "%~dp0"

echo ===================================================
echo   Сборка установщика AMURKA PVE Launcher (C#)
echo ===================================================
echo.

echo 1. Публикация автономной C# сборки...
dotnet publish .\AmurkaLauncher\AmurkaLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\AmurkaLauncher\publish-standalone

if %ERRORLEVEL% NEQ 0 (
    echo Ошибка при публикации C# проекта.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo 2. Компиляция установщика NSIS...
set "MAKENSIS="
if exist "C:\Users\kiric\AppData\Local\electron-builder\Cache\nsis\nsis-3.0.4.1\Bin\makensis.exe" (
    set "MAKENSIS=C:\Users\kiric\AppData\Local\electron-builder\Cache\nsis\nsis-3.0.4.1\Bin\makensis.exe"
) else if exist "C:\Program Files (x86)\NSIS\makensis.exe" (
    set "MAKENSIS=C:\Program Files (x86)\NSIS\makensis.exe"
) else (
    for /f "delims=" %%I in ('where makensis 2^>nul') do set "MAKENSIS=%%I"
)

if "%MAKENSIS%"=="" (
    echo Ошибка: makensis.exe не найден.
    pause
    exit /b 1
)

"%MAKENSIS%" .\installer.nsi

if %ERRORLEVEL% NEQ 0 (
    echo Ошибка при сборке инсталлятора.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ===================================================
echo   Установщик успешно собран!
echo   Файл: dist\AMURKA-PVE-Launcher-Setup-2.1.17.exe
echo ===================================================
echo.
pause
