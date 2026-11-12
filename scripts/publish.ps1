param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/publish-platform' }
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output already exists; choose a new -OutputDirectory: $output" }
foreach ($key in @('Paperwork__AzureKey', 'Paperwork__JevApiKey')) {
    if ([Environment]::GetEnvironmentVariable($key)) {
        throw 'Publish from a shell without provider credentials. Inject credentials only when running the finished package.'
    }
}
foreach ($oneDrive in @($env:OneDrive, $env:OneDriveCommercial, $env:OneDriveConsumer)) {
    if ($oneDrive -and ($output.Equals($oneDrive, [StringComparison]::OrdinalIgnoreCase) -or
        $output.StartsWith($oneDrive.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
        throw 'Publish output must be outside OneDrive.'
    }
}
$stage = Join-Path $root ('artifacts/package-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $stage
Push-Location $root
try {
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Pinned tool restore failed.' }
    & dotnet csharpier check server
    if ($LASTEXITCODE -ne 0) { throw 'Backend formatting check failed.' }
    Push-Location (Join-Path $root 'client')
    try {
        & npm.cmd ci
        if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed.' }
        & npm.cmd run build -- --configuration production
        if ($LASTEXITCODE -ne 0) { throw 'Frontend production build failed.' }
    } finally { Pop-Location }
    & dotnet publish server/PaperworkClassifier.Api.csproj --configuration Release --self-contained false --output $stage
    if ($LASTEXITCODE -ne 0) { throw 'Backend publish failed.' }
    $browser = Join-Path $root 'client/dist/client/browser'
    if (-not (Test-Path -LiteralPath "$browser/index.html")) { throw 'Angular index.html was not produced.' }
    $null = New-Item -ItemType Directory -Path "$stage/wwwroot" -Force
    Copy-Item -Path "$browser/*" -Destination "$stage/wwwroot" -Recurse
    Copy-Item -LiteralPath "$root/scripts/run-published.ps1" -Destination "$stage/run-published.ps1"
    Copy-Item -LiteralPath "$root/docs/packaging.md" -Destination "$stage/packaging.md"
    Copy-Item -LiteralPath "$root/docs/wolverine.md" -Destination "$stage/wolverine.md"
    Copy-Item -LiteralPath "$root/docs/workflows.md" -Destination "$stage/workflows.md"
    foreach ($name in @('platform-contract.md', 'platform-verification.md', 'quality-platform.md', 'connectors-platform.md', 'operations-platform.md', 'extraction-platform.md', 'flows-platform.md')) {
        Copy-Item -LiteralPath "$root/docs/$name" -Destination (Join-Path $stage $name)
    }
    foreach ($file in @('PaperworkClassifier.Api.dll', 'PaperworkClassifier.Api.runtimeconfig.json', 'wwwroot/index.html', 'wwwroot/assets/pdf.worker.min.mjs')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $file))) { throw "Package is missing $file" }
    }
    $null = New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force
    if (Test-Path -LiteralPath $output) { throw "Output appeared during build; refusing to overwrite: $output" }
    Move-Item -LiteralPath $stage -Destination $output
    Write-Host "Published to $output"
    Write-Host "Run: pwsh -NoProfile -File `"$output/run-published.ps1`""
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
