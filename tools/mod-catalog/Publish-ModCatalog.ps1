param(
    [string]$Repository = 'AMURKA-PVE-SCUM/amurka-pve-mods'
)

$ErrorActionPreference = 'Stop'
function Get-GitHubJson([string]$Endpoint) {
    $outputJson = gh api $Endpoint
    if ($LASTEXITCODE -ne 0) { throw "GitHub request failed: $Endpoint" }
    return $outputJson | ConvertFrom-Json
}

$revision = (Get-GitHubJson "repos/$Repository/commits/main").sha
$tree = Get-GitHubJson "repos/$Repository/git/trees/${revision}?recursive=1"
if ($tree.truncated) { throw 'Incomplete tree: refusing to publish a partial catalog' }
$files = @($tree.tree | Where-Object {
    $_.type -eq 'blob' -and $_.path -match '^(core|speed|skins|weapons|buildings|music)/[^/]+\.(pak|sig)$'
} | Sort-Object path | ForEach-Object { [ordered]@{path=$_.path;sha=$_.sha;size=$_.size} })
if ($files.Count -eq 0) { throw 'Empty catalog: refusing to publish' }
$duplicateNames = $files | Group-Object { ($_.path -split '/')[1].ToLowerInvariant() } | Where-Object Count -gt 1
if ($duplicateNames) { throw 'Duplicate mod filenames across categories' }
$content = [ordered]@{schemaVersion=1;revision=$revision;files=$files} | ConvertTo-Json -Depth 5
$payload = @{
    message='chore: update launcher mod catalog'
    branch='main'
    content=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($content))
}
$existing = gh api "repos/$Repository/contents/mods.json?ref=main" 2>$null
if ($LASTEXITCODE -eq 0) { $payload.sha = ($existing | ConvertFrom-Json).sha }
if ((Get-GitHubJson "repos/$Repository/git/ref/heads/main").object.sha -ne $revision) {
    throw 'The branch changed while generating the catalog. Run this script again.'
}
$payloadFile = [IO.Path]::GetTempFileName()
try {
    $payload | ConvertTo-Json | Set-Content -LiteralPath $payloadFile -Encoding utf8
    gh api --method PUT "repos/$Repository/contents/mods.json" --input $payloadFile --jq .commit.sha
    if ($LASTEXITCODE -ne 0) { throw 'Failed to publish catalog' }
    Write-Output "Published $($files.Count) files for $revision"
}
finally { Remove-Item -LiteralPath $payloadFile -ErrorAction SilentlyContinue }
