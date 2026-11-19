param(
    [Parameter(Mandatory)][string]$BackupDirectory,
    [Parameter(Mandatory)][string]$DataDirectory,
    [switch]$CoordinatedBlobRestore
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue) { throw 'Stop paprika before restoring.' }
$backup = (Resolve-Path -LiteralPath $BackupDirectory).Path
$target = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\', '/')
foreach ($path in @($backup, $target)) {
    $ancestor = $path
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse-point paths are not supported.' }
        $ancestor = Split-Path $ancestor -Parent
    }
}
if ($target.StartsWith($backup.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Restore destination must be outside the backup directory.' }
if (Test-Path -LiteralPath $target) { throw 'Restore requires a NEW data directory. Existing data will not be overwritten.' }
foreach ($oneDrive in @($env:OneDrive, $env:OneDriveCommercial, $env:OneDriveConsumer)) {
    if ($oneDrive -and ($target.Equals($oneDrive, [StringComparison]::OrdinalIgnoreCase) -or $target.StartsWith($oneDrive.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) { throw 'Restore runtime data outside OneDrive.' }
}
if ((Get-Item -LiteralPath $backup).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse-point backups are not supported.' }
if (Get-ChildItem -LiteralPath $backup -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Restore cannot follow reparse points.' }
$manifest = Get-Content -LiteralPath "$backup/manifest.json" -Raw | ConvertFrom-Json
if ($manifest.format -ne 'paperwork-backup-1' -or -not $manifest.files.Count) { throw 'Unsupported or empty backup manifest.' }
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$verified = @()
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    # Require canonical relative segments; reject traversal, ADS, device names and Windows aliases.
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative)) { throw 'Invalid path in backup manifest.' }
    foreach ($segment in ($relative -split '[\\/]')) {
        if (-not $segment -or $segment -in @('.', '..') -or $segment -match '[<>:"|?*\x00-\x1f]|[. ]$|^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)') { throw 'Invalid path in backup manifest.' }
    }
    if (-not $names.Add($relative.Replace('\', '/'))) { throw 'Duplicate path in backup manifest.' }
    $file = Join-Path "$backup/data" $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'A backup file is missing.' }
    if ((Get-Item -LiteralPath $file).Length -ne $entry.length -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'Backup integrity validation failed.' }
    $verified += @{ path = $relative; source = $file; length = $entry.length; sha256 = $entry.sha256 }
}
if (-not $names.Contains('jobs.sqlite')) { throw 'Backup has no packet database.' }
if ($names.Contains('azure-artifacts.required') -and -not $CoordinatedBlobRestore) {
    throw 'This backup needs its matching private Blob container. Restore and verify the container, then use -CoordinatedBlobRestore. See docs/storage.md.'
}
$stage = $target + '.staging-' + [Guid]::NewGuid().ToString('N')
try {
    $null = New-Item -ItemType Directory -Path $stage -Force
    foreach ($entry in $verified) {
        $destination = Join-Path $stage $entry.path
        $null = New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force
        Copy-Item -LiteralPath $entry.source -Destination $destination
        if ((Get-Item -LiteralPath $destination).Length -ne $entry.length -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'Backup changed during restore; no recovered directory was published.' }
    }
    if (Test-Path -LiteralPath $target) { throw 'Destination appeared during restore; refusing to overwrite it.' }
    Move-Item -LiteralPath $stage -Destination $target
    Write-Host "Restored to $target. Set paprika__DataDirectory to this folder to use the recovered data."
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
