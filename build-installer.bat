@echo off
chcp 65001 >nul
title Сборка брендированного установщика AMURKA PVE Launcher
cd /d "%~dp0"

echo ===================================================
echo   Сборка брендированного установщика (C# / WPF)
echo ===================================================
echo.

echo 1. Определение версии из AmurkaLauncher.csproj...
for /f "usebackq tokens=*" %%i in (`powershell -NoProfile -Command "([xml](Get-Content '.\AmurkaLauncher\AmurkaLauncher.csproj')).Project.PropertyGroup.Version | Select-Object -First 1"`) do set "APP_VERSION=%%i"

if "%APP_VERSION%"=="" (
    set "APP_VERSION=2.1.26"
)
echo Версия сборки: v%APP_VERSION%
echo.

echo 2. Публикация автономного C# клиента (.NET 8 LTS)...
dotnet publish .\AmurkaLauncher\AmurkaLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\AmurkaLauncher\publish-standalone

if %ERRORLEVEL% NEQ 0 (
    echo [ОШИБКА] Ошибка при публикации C# проекта лаунчера.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo 3. Упаковка файлов лаунчера в payload.zip...
powershell -NoProfile -Command ^
    "if (Test-Path 'AmurkaInstaller\payload.zip') { Remove-Item 'AmurkaInstaller\payload.zip' -Force }; " ^
    "Compress-Archive -Path 'AmurkaLauncher\publish-standalone\*' -DestinationPath 'AmurkaInstaller\payload.zip' -Force; " ^
    "$len = (Get-Item 'AmurkaInstaller\payload.zip').Length; " ^
    "Write-Host '  -> payload.zip создан, размер: ' ([math]::Round($len / 1MB, 2)) 'МБ'"

echo.
echo 4. Сборка брендированного C# WPF установщика...
dotnet publish .\AmurkaInstaller\AmurkaInstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\AmurkaInstaller\publish

if %ERRORLEVEL% NEQ 0 (
    echo [ОШИБКА] Ошибка при сборке проекта установщика.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo 5. Формирование итогового EXE и обновление dist\launcher.json...
powershell -NoProfile -Command ^
    "if (-not (Test-Path 'dist')) { New-Item -ItemType Directory -Path 'dist' | Out-Null }; " ^
    "$target = 'dist\AMURKA-PVE-Launcher-Setup-' + $env:APP_VERSION + '.exe'; " ^
    "Copy-Item 'AmurkaInstaller\publish\AMURKA PVE Launcher Setup.exe' $target -Force; " ^
    "$hash = (Get-FileHash $target -Algorithm SHA512).Hash; " ^
    "$size = (Get-Item $target).Length; " ^
    "$json = [PSCustomObject]@{ sha512 = $hash; version = $env:APP_VERSION; size = $size; file = [System.IO.Path]::GetFileName($target) } | ConvertTo-Json; " ^
    "[System.IO.File]::WriteAllText('dist\launcher.json', $json, [System.Text.Encoding]::UTF8); " ^
    "Write-Host '  -> Готово: ' $target; " ^
    "Write-Host '  -> Размер: ' ([math]::Round($size / 1MB, 2)) 'МБ'; " ^
    "Write-Host '  -> SHA-512: ' $hash.Substring(0, 16) '...'"

echo.
echo ===================================================
echo   Брендированный установщик успешно собран:
echo   dist\AMURKA-PVE-Launcher-Setup-%APP_VERSION%.exe
echo ===================================================
echo.
pause