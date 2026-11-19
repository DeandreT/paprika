param(
    [string]$DataDirectory = $env:paprika__DataDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$CoordinatedBlobBackup
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $DataDirectory) {
    $DataDirectory = Join-Path $env:LOCALAPPDATA 'paprika'
    $legacyDirectory = Join-Path $env:LOCALAPPDATA 'PaperworkClassifier'
    if (-not (Test-Path -LiteralPath "$DataDirectory/jobs.sqlite" -PathType Leaf) -and (Test-Path -LiteralPath "$legacyDirectory/jobs.sqlite" -PathType Leaf)) {
        $DataDirectory = $legacyDirectory
    }
}
if (Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue) {
    throw 'Stop paprika before making an offline backup.'
}
$source = (Resolve-Path -LiteralPath $DataDirectory).Path.TrimEnd('\', '/')
$target = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
# Check ancestors too: a normal-looking child can live beneath a junction.
foreach ($path in @($source, $target)) {
    $ancestor = $path
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse-point paths are not supported.' }
        $ancestor = Split-Path $ancestor -Parent
    }
}
if ($target.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or $target.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Backup destination must be outside the data directory.'
}
foreach ($oneDrive in @($env:OneDrive, $env:OneDriveCommercial, $env:OneDriveConsumer)) {
    if ($oneDrive -and ($target.Equals($oneDrive, [StringComparison]::OrdinalIgnoreCase) -or $target.StartsWith($oneDrive.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
        throw 'Runtime backups must stay in local non-OneDrive storage.'
    }
}
if (Test-Path -LiteralPath $target) { throw 'Backup destination already exists; choose a new folder.' }
if (-not (Test-Path -LiteralPath "$source/jobs.sqlite")) { throw 'The source is not a paprika data directory.' }
if ((Test-Path -LiteralPath "$source/azure-artifacts.required") -and -not $CoordinatedBlobBackup) {
    throw 'This data directory uses Azure Blob storage. Stop all writers, back up the dedicated private container and use -CoordinatedBlobBackup for the matching local copy. See docs/storage.md.'
}
if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse-point data roots are not supported.' }
$entries = @(Get-ChildItem -LiteralPath $source -Recurse -Force)
if ($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Backups cannot follow reparse points.' }
$stage = $target + '.staging-' + [Guid]::NewGuid().ToString('N')
try {
    $null = New-Item -ItemType Directory -Path "$stage/data" -Force
    $manifest = @()
    foreach ($file in $entries | Where-Object { -not $_.PSIsContainer }) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        $destination = Join-Path "$stage/data" $relative
        $null = New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force
        # Refuse files currently opened by another process rather than taking a knowingly inconsistent copy.
        $input = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try {
            $output = [IO.File]::Create($destination)
            try { $input.CopyTo($output) } finally { $output.Dispose() }
        } finally { $input.Dispose() }
        $manifest += @{ path = $relative.Replace('\', '/'); length = (Get-Item -LiteralPath $destination).Length; sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    @{ format = 'paperwork-backup-1'; createdAt = [DateTimeOffset]::UtcNow.ToString('O'); files = $manifest } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$stage/manifest.json" -Encoding utf8NoBOM
    $null = New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force
    if (Test-Path -LiteralPath $target) { throw 'Destination appeared during backup; refusing to overwrite it.' }
    Move-Item -LiteralPath $stage -Destination $target
    Write-Host "Backup created: $target ($($manifest.Count) files). Contains private document data; keep local and access-controlled."
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
