@echo off
chcp 65001 >nul
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1"
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ОШИБКА] Сборка завершилась с ошибкой (%ERRORLEVEL%).
    pause
    exit /b %ERRORLEVEL%
)
pause
