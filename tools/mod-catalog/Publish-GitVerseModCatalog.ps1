param(
    [Parameter(Mandatory = $true)][string]$RepositoryPath,
    [string]$Branch = 'main',
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $RepositoryPath).Path
function Invoke-RepoGit([string[]]$Arguments) {
    $result = & git -C $repo @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git command failed: $($Arguments[0])" }
    return $result
}
$top = Invoke-RepoGit @('rev-parse', '--show-toplevel')
if (![string]::Equals([IO.Path]::GetFullPath($top).TrimEnd('\','/'), $repo.TrimEnd('\','/'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'RepositoryPath must point to the root of the mods repository.'
}
if (Invoke-RepoGit @('status', '--porcelain')) { throw 'Commit the mod changes before generating the catalog.' }
if ((Invoke-RepoGit @('branch', '--show-current')) -cne $Branch) { throw "Check out branch $Branch first." }
if ($Publish) {
    $remote = Invoke-RepoGit @('remote', 'get-url', 'origin')
    if ($remote -notmatch '(?i)gitverse\.ru[:/]+DOMO/amurka-pve-mods(?:\.git)?/?$') {
        throw 'Publishing is restricted to the DOMO/amurka-pve-mods repository on GitVerse.'
    }
}
$revision = Invoke-RepoGit @('rev-parse', 'HEAD')
$rows = Invoke-RepoGit @('-c', 'core.quotepath=false', 'ls-tree', '-r', '-l', '--full-tree', $revision)
$files = @()
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in $rows) {
    if ($line -notmatch '^\d+\s+blob\s+([0-9a-f]{40})\s+(\d+)\t((?:core|speed|skins|weapons|buildings|music)/([^/]+\.(?:pak|sig)))$') { continue }
    $sha = $Matches[1]; $size = [long]$Matches[2]; $path = $Matches[3]; $name = $Matches[4]
    if ($name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or !$names.Add($name)) {
        throw "Invalid or duplicate mod filename: $name"
    }
    if ($size -lt 1024) {
        $small = (Invoke-RepoGit @('show', "${revision}:$path")) -join "`n"
        if ($small.StartsWith('version https://git-lfs.github.com/spec/v1')) {
            throw "Git LFS pointer found instead of mod bytes: $path"
        }
    }
    $files += [ordered]@{ path = $path; sha = $sha; size = $size }
}
if (!$files.Count) { throw 'No mods found; refusing to publish an empty catalog.' }
$catalog = [ordered]@{ schemaVersion = 1; revision = $revision; files = @($files | Sort-Object path) }
$json = ($catalog | ConvertTo-Json -Depth 5) + "`n"
[IO.File]::WriteAllText((Join-Path $repo 'mods.json'), $json, [Text.UTF8Encoding]::new($false))
if ($Publish) {
    $null = Invoke-RepoGit @('add', '--', 'mods.json')
    $null = Invoke-RepoGit @('commit', '-m', 'Update GitVerse launcher mod catalog')
    $null = Invoke-RepoGit @('push', 'origin', "HEAD:refs/heads/$Branch")
}
Write-Output "GitVerse catalog ready: $($files.Count) files at $revision"
