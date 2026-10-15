@echo off
title Сборка установщика AMURKA PVE Launcher (C#)
cd /d "%~dp0"

echo ===================================================
echo   Сборка установщика AMURKA PVE Launcher (C#)
echo ===================================================
echo.

echo 1. Публикация автономной C# сборки (.NET 8 LTS)...
dotnet publish .\AmurkaLauncher\AmurkaLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\AmurkaLauncher\publish-standalone

if %ERRORLEVEL% NEQ 0 (
    echo [ОШИБКА] Ошибка при публикации C# проекта.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo 2. Поиск компилятора NSIS (makensis.exe)...
set "MAKENSIS="
if exist "%LOCALAPPDATA%\electron-builder\Cache\nsis\nsis-3.0.4.1\Bin\makensis.exe" (
    set "MAKENSIS=%LOCALAPPDATA%\electron-builder\Cache\nsis\nsis-3.0.4.1\Bin\makensis.exe"
) else if exist "C:\Program Files (x86)\NSIS\makensis.exe" (
    set "MAKENSIS=C:\Program Files (x86)\NSIS\makensis.exe"
) else if exist "C:\Program Files\NSIS\makensis.exe" (
    set "MAKENSIS=C:\Program Files\NSIS\makensis.exe"
) else (
    for /f "delims=" %%I in ('where makensis 2^>nul') do set "MAKENSIS=%%I"
)

if "%MAKENSIS%"=="" (
    echo [ОШИБКА] makensis.exe не найден. Установите NSIS (https://nsis.sourceforge.io/).
    pause
    exit /b 1
)

echo Используется компилятор: %MAKENSIS%
echo.
echo 3. Компиляция установщика NSIS...
"%MAKENSIS%" .\installer.nsi

if %ERRORLEVEL% NEQ 0 (
    echo [ОШИБКА] Ошибка при сборке инсталлятора.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ===================================================
echo   Установщик успешно собран в папке dist\
echo ===================================================
echo.
pause
