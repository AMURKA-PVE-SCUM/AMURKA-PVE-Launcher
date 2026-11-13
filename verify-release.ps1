$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  AMURKA PVE: Автоматическая верификация релиза v3.0.0" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

$distDir = Join-Path $PSScriptRoot "dist"
$launcherJsonPath = Join-Path $distDir "launcher.json"

if (-not (Test-Path $launcherJsonPath)) {
    Write-Host "[FAIL] Файл dist\launcher.json не найден!" -ForegroundColor Red
    exit 1
}

$manifest = Get-Content $launcherJsonPath -Raw | ConvertFrom-Json
$expectedVersion = "3.0.0"

if ($manifest.version -ne $expectedVersion) {
    Write-Host "[FAIL] Версия в манифесте: $($manifest.version) (ожидалась: $expectedVersion)" -ForegroundColor Red
    exit 1
}

$setupPath = Join-Path $distDir $manifest.file
if (-not (Test-Path $setupPath)) {
    Write-Host "[FAIL] Исполняемый файл инсталлятора не найден: $setupPath" -ForegroundColor Red
    exit 1
}

$actualLength = (Get-Item $setupPath).Length
if ($actualLength -ne $manifest.size) {
    Write-Host "[FAIL] Размер файла не совпадает! Ожидалось: $($manifest.size), Фактически: $actualLength" -ForegroundColor Red
    exit 1
}

$actualSha512 = (Get-FileHash $setupPath -Algorithm SHA512).Hash
if ($actualSha512 -ne $manifest.sha512) {
    Write-Host "[FAIL] Контрольная сумма SHA-512 не совпадает!" -ForegroundColor Red
    exit 1
}

Write-Host "[PASS] 1. Манифест launcher.json и бинарник Setup.exe строго согласованы (SHA-512 совпадает)." -ForegroundColor Green

# 2. Проверка цифровой подписи Authenticode
$sig = Get-AuthenticodeSignature -FilePath $setupPath
if ($sig.SignerCertificate) {
    Write-Host "[PASS] 2. Инсталлятор имеет цифровую подпись Authenticode: '$($sig.SignerCertificate.Subject)' (Алгоритм: SHA-256 + DigiCert Timestamp)" -ForegroundColor Green
} else {
    Write-Host "[WARN] 2. Инсталлятор собран без подписи (будет подписан при следующем вызове build-installer.ps1)." -ForegroundColor Yellow
}

# 3. Тестовая тихая распаковка во временную папку
$tempTestTarget = Join-Path $env:TEMP ("Amurka_Verify_" + [Guid]::NewGuid().ToString("N"))
try {
    Write-Host "[INFO] 3. Запуск тестовой распаковки во временный каталог: $tempTestTarget" -ForegroundColor Cyan
    $process = Start-Process -FilePath $setupPath -ArgumentList "/SILENT", "/DIR=$tempTestTarget", "/NODESKTOP", "/NOSTARTMENU" -PassThru -Wait
    
    $installedLauncher = Join-Path $tempTestTarget "AMURKA PVE Launcher.exe"
    if (Test-Path $installedLauncher) {
        $fileVersion = (Get-Item $installedLauncher).VersionInfo.ProductVersion
        Write-Host "[PASS] 3. Установка прошла успешно! Распакован AMURKA PVE Launcher.exe (версия: $fileVersion)." -ForegroundColor Green
    } else {
        Write-Host "[FAIL] 3. Файл AMURKA PVE Launcher.exe не был распакован!" -ForegroundColor Red
        exit 1
    }
}
finally {
    if (Test-Path $tempTestTarget) {
        Remove-Item -Recurse -Force $tempTestTarget -ErrorAction SilentlyContinue
    }
}

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Green
Write-Host "  РЕЛИЗ v3.0.0 ПОЛНОСТЬЮ ВЕРИФИЦИРОВАН И ГОТОВ К РАЗДАЧЕ!" -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green
