param(
    [string]$ModsRepoPath = "..\amurka-pve-mods"
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$targetDir = Resolve-Path (Join-Path $PSScriptRoot $ModsRepoPath) -ErrorAction SilentlyContinue
if (-not $targetDir) {
    Write-Host "[ERROR] Репозиторий модов не найден по пути: $ModsRepoPath" -ForegroundColor Red
    Write-Host "Использование: .\build-catalog.ps1 -ModsRepoPath 'C:\path\to\amurka-pve-mods'" -ForegroundColor Yellow
    exit 1
}

Write-Host "Сборка утилиты генерации каталога..." -ForegroundColor Cyan
dotnet run --project .\Amurka.CatalogBuilder\Amurka.CatalogBuilder.csproj -c Release -- $targetDir.Path
if ($LASTEXITCODE -eq 0) {
    Write-Host "`nФайл mods.json успешно создан. Закоммитьте и отправьте его на GitVerse!" -ForegroundColor Green
}
