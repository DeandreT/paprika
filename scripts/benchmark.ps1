param([string]$PackageDirectory, [string]$ReportPath, [string]$Python = 'python')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
foreach ($command in @('dotnet', 'npm.cmd', $Python)) { $null = Get-Command $command -ErrorAction Stop }
if (-not $PackageDirectory) { $PackageDirectory = Join-Path $root 'artifacts/publish-platform' }
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
if (-not (Test-Path -LiteralPath "$package/PaperworkClassifier.Api.dll")) { throw 'Publish the application first.' }
if (-not (Test-Path -LiteralPath "$root/tests/node_modules/@playwright/test")) { throw 'Run npm ci from tests first.' }
if (Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 5080 is in use. Stop the local API before running the isolated benchmark.' }
if (-not $ReportPath) {
    $ReportPath = Join-Path $root 'artifacts/benchmark-report.json'
    if (Test-Path -LiteralPath $ReportPath) { $ReportPath = Join-Path $root ('artifacts/benchmark-report-' + [Guid]::NewGuid().ToString('N') + '.json') }
}
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
if (Test-Path -LiteralPath $ReportPath) { throw 'The requested report already exists. Choose a new report path.' }
$null = New-Item -ItemType Directory -Path (Split-Path $ReportPath -Parent) -Force
$data = Join-Path $env:LOCALAPPDATA ('Temp/opencode/paperwork-benchmark-' + [Guid]::NewGuid().ToString('N'))
$info = [System.Diagnostics.ProcessStartInfo]::new()
$info.FileName = 'dotnet'
$info.WorkingDirectory = $package
$info.UseShellExecute = $false
foreach ($argument in @("$package/PaperworkClassifier.Api.dll", '--contentRoot', $package, '--urls', 'http://127.0.0.1:5080', '--Logging:LogLevel:Default=Warning', '--Paperwork:Mode=Simulation', "--Paperwork:DataDirectory=$data", '--Paperwork:AzureKey=', '--Paperwork:JevApiKey=')) { $info.ArgumentList.Add($argument) }
$info.Environment['Paperwork__Mode'] = 'Simulation'
$info.Environment['Paperwork__DataDirectory'] = $data
foreach ($key in @('Paperwork__AzureKey', 'Paperwork__JevApiKey')) { $null = $info.Environment.Remove($key) }
$previous = @{}
foreach ($key in @('PAPERWORK_TEST_URL', 'PAPERWORK_BENCHMARK_ISOLATED', 'PAPERWORK_BENCHMARK_REPORT', 'PAPERWORK_BENCHMARK_PYTHON')) { $previous[$key] = [Environment]::GetEnvironmentVariable($key) }
$api = $null
try {
    $api = [System.Diagnostics.Process]::Start($info)
    $ready = $false
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ([DateTimeOffset]::UtcNow -lt $deadline -and -not $api.HasExited) {
        try {
            $health = Invoke-RestMethod 'http://127.0.0.1:5080/api/health' -TimeoutSec 2
            if ($health.status -eq 'ok') { $ready = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'Published API did not become ready.' }
    $env:PAPERWORK_TEST_URL = 'http://127.0.0.1:5080'
    $env:PAPERWORK_BENCHMARK_ISOLATED = '1'
    $env:PAPERWORK_BENCHMARK_REPORT = $ReportPath
    $env:PAPERWORK_BENCHMARK_PYTHON = $Python
    Push-Location "$root/tests"
    try {
        & npm.cmd test -- --config benchmark.config.ts
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic benchmark invariants failed.' }
    } finally { Pop-Location }
    Write-Host "Synthetic benchmark report: $ReportPath"
} finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
    if ($null -ne $api) {
        if (-not $api.HasExited) { $api.Kill($true); $api.WaitForExit() }
        $api.Dispose()
    }
    if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
    Write-Host 'Benchmark API stopped; isolated application data removed.'
}
