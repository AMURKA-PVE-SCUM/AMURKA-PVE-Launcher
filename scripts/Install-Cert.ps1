param(
    [string]$CertificatePath = "$PSScriptRoot\AmurkaCodeSigning.cer"
)

$ErrorActionPreference = 'Stop'

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  AMURKA PVE: Установка сертификата доверия" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

if (-not (Test-Path $CertificatePath)) {
    Write-Host "[ERROR] Файл сертификата не найден: $CertificatePath" -ForegroundColor Red
    exit 1
}

try {
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertificatePath)
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        [System.Security.Cryptography.X509Certificates.StoreName]::TrustedPublisher,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
    )
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $store.Add($cert)
    $store.Close()

    Write-Host "[SUCCESS] Сертификат '$($cert.Subject)' успешно добавлен в доверенные издатели текущего пользователя!" -ForegroundColor Green
    Write-Host "[INFO] Теперь Windows SmartScreen и Защитник будут доверять лаунчеру AMURKA PVE без предупреждений." -ForegroundColor Yellow
}
catch {
    Write-Host "[ERROR] Не удалось установить сертификат: $_" -ForegroundColor Red
    exit 1
}
