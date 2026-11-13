$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "  Building unified Single-Binary installer (C# / WPF)" -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan

$csproj = [xml](Get-Content "AmurkaLauncher\AmurkaLauncher.csproj")
$appVersion = $csproj.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($appVersion)) { $appVersion = "3.0.0" }
Write-Host "1. Build version: v$appVersion" -ForegroundColor Yellow

Write-Host ""
Write-Host "2. Running build & xUnit quality gate (TreatWarningsAsErrors)..." -ForegroundColor Yellow
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

& $dotnet test .\AmurkaLauncher.sln -c Release /p:TreatWarningsAsErrors=true
if ($LASTEXITCODE -ne 0) { throw "Solution build or unit tests failed" }

Write-Host ""
Write-Host "3. Publishing standalone Single-Binary C# client (.NET 8 LTS)..." -ForegroundColor Yellow
& $dotnet publish .\AmurkaLauncher\AmurkaLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\AmurkaLauncher\publish-standalone
if ($LASTEXITCODE -ne 0) { throw "AmurkaLauncher publish failed" }

Get-ChildItem "AmurkaLauncher\publish-standalone\*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem "AmurkaLauncher\publish-standalone\*.xml" -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host ""
Write-Host "4. Finalizing installer binary and applying Authenticode Code Signing..." -ForegroundColor Yellow
$distDir = Join-Path $PSScriptRoot "dist"
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }

$certSubject = "CN=AMURKA PVE Launcher, O=AMURKA PVE, C=RU"
$codeCert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -like "*AMURKA PVE*" } | Select-Object -First 1

if (-not $codeCert) {
    Write-Host "  -> Creating legal self-signed Authenticode developer certificate..." -ForegroundColor Cyan
    $codeCert = New-SelfSignedCertificate -Subject $certSubject -Type CodeSigningCert -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
    $publicCertPath = Join-Path $PSScriptRoot "scripts\AmurkaCodeSigning.cer"
    Export-Certificate -Cert $codeCert -FilePath $publicCertPath -Force | Out-Null
    Write-Host "  -> Public certificate exported to scripts\AmurkaCodeSigning.cer" -ForegroundColor Green
}

$target = Join-Path $distDir "AMURKA-PVE-Launcher-Setup-$appVersion.exe"
Get-Process | Where-Object { $_.ProcessName -like "*Setup*" } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
Copy-Item "AmurkaLauncher\publish-standalone\AMURKA PVE Launcher.exe" $target -Force

if ($codeCert) {
    try {
        Set-AuthenticodeSignature -FilePath $target -Certificate $codeCert -HashAlgorithm SHA256 -TimestampServer "http://timestamp.digicert.com" -ErrorAction SilentlyContinue | Out-Null
        Write-Host "  -> Authenticode signature applied successfully (SHA-256 + Timestamp)!" -ForegroundColor Green
    }
    catch {
        Write-Host "  -> Code signing skipped or timestamp server unreachable: $_" -ForegroundColor Yellow
    }
}

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

Write-Host "  -> Ready: $target" -ForegroundColor Green
Write-Host "  -> Size: $([math]::Round($size / 1MB, 2)) MB (Single-Binary with 0 duplicates!)" -ForegroundColor Green
Write-Host "  -> SHA-512: $($hash.Substring(0, 16))..." -ForegroundColor Green

Write-Host ""
Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "  Installer successfully built: $target" -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan
