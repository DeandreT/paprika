param([string]$PackageDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $PackageDirectory) {
    $PackageDirectory = if (Test-Path -LiteralPath "$PSScriptRoot/paprika.Api.dll") {
        $PSScriptRoot
    } else {
        Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/publish-platform'
    }
}
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
foreach ($file in @('paprika.Api.dll', 'wwwroot/index.html')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $file))) { throw "Package is missing $file" }
}
Push-Location $package
try {
    Write-Host 'paprika: http://127.0.0.1:5080. Ctrl+C stops the API and UI.'
    & dotnet (Join-Path $package 'paprika.Api.dll') --contentRoot $package
    if ($LASTEXITCODE -ne 0) { throw "Published API exited with code $LASTEXITCODE." }
} finally { Pop-Location }
