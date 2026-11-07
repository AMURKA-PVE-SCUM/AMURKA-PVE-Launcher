$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "  Сборка брендированного установщика (C# / WPF)" -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan

$csproj = [xml](Get-Content "AmurkaLauncher\AmurkaLauncher.csproj")
$appVersion = $csproj.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($appVersion)) { $appVersion = "2.1.27" }
Write-Host "1. Версия сборки: v$appVersion" -ForegroundColor Yellow

Write-Host ""
Write-Host "2. Публикация автономного C# клиента (.NET 8 LTS)..." -ForegroundColor Yellow
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

& $dotnet publish .\AmurkaLauncher\AmurkaLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o .\AmurkaLauncher\publish-standalone
if ($LASTEXITCODE -ne 0) { throw "Ошибка публикации AmurkaLauncher" }

Write-Host ""
Write-Host "3. Упаковка файлов лаунчера в payload.zip..." -ForegroundColor Yellow
if (Test-Path "AmurkaInstaller\payload.zip") { Remove-Item "AmurkaInstaller\payload.zip" -Force }
Get-ChildItem "AmurkaLauncher\publish-standalone\*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force
Compress-Archive -Path "AmurkaLauncher\publish-standalone\*" -DestinationPath "AmurkaInstaller\payload.zip" -CompressionLevel Optimal -Force
$zipLen = (Get-Item "AmurkaInstaller\payload.zip").Length
Write-Host "  -> payload.zip создан, размер: $([math]::Round($zipLen / 1MB, 2)) МБ" -ForegroundColor Green

Write-Host ""
Write-Host "4. Сборка брендированного C# WPF установщика..." -ForegroundColor Yellow
& $dotnet publish .\AmurkaInstaller\AmurkaInstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\AmurkaInstaller\publish
if ($LASTEXITCODE -ne 0) { throw "Ошибка сборки AmurkaInstaller" }

Write-Host ""
Write-Host "5. Формирование итогового EXE и обновление dist\launcher.json..." -ForegroundColor Yellow
$distDir = Join-Path $PSScriptRoot "dist"
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }
$target = Join-Path $distDir "AMURKA-PVE-Launcher-Setup-$appVersion.exe"
Copy-Item "AmurkaInstaller\publish\AMURKA PVE Launcher Setup.exe" $target -Force
$hash = (Get-FileHash $target -Algorithm SHA512).Hash
$size = (Get-Item $target).Length

$json = [PSCustomObject]@{
    version = $appVersion
    file = [System.IO.Path]::GetFileName($target)
    size = $size
    sha512 = $hash
} | ConvertTo-Json -Depth 4

$jsonPath = Join-Path $distDir "launcher.json"
[System.IO.File]::WriteAllText($jsonPath, $json, [System.Text.Encoding]::UTF8)

Write-Host "  -> Готово: $target" -ForegroundColor Green
Write-Host "  -> Размер: $([math]::Round($size / 1MB, 2)) МБ" -ForegroundColor Green
Write-Host "  -> SHA-512: $($hash.Substring(0, 16))..." -ForegroundColor Green

Write-Host ""
Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "  Установщик успешно собран: $target" -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan
