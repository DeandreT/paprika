param([string]$PackageDirectory, [string[]]$TestFiles)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
foreach ($key in @('paprika__AzureKey', 'paprika__JevApiKey')) {
    if ([Environment]::GetEnvironmentVariable($key)) {
        throw 'Run synthetic acceptance checks without provider credentials; only the live API should receive them.'
    }
}
if (-not $PackageDirectory) { $PackageDirectory = Join-Path $root 'artifacts/publish-platform' }
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
if (-not (Test-Path -LiteralPath "$package/paprika.Api.dll")) { throw 'Publish the application first.' }
if (-not (Test-Path -LiteralPath "$root/tests/node_modules/@playwright/test")) { throw 'Run npm ci from tests and install Playwright Chromium first.' }
if (Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue) {
    throw 'Port 5080 is already in use. Stop your local API before running isolated acceptance checks.'
}

$data = Join-Path $env:LOCALAPPDATA ('Temp/opencode/paprika-acceptance-' + [Guid]::NewGuid().ToString('N'))
$info = [System.Diagnostics.ProcessStartInfo]::new()
$info.FileName = 'dotnet'
$info.WorkingDirectory = $package
$info.UseShellExecute = $false
foreach ($argument in @("$package/paprika.Api.dll", '--contentRoot', $package, '--Logging:LogLevel:Microsoft.AspNetCore=Warning')) { $info.ArgumentList.Add($argument) }
$info.Environment['paprika__Mode'] = 'Simulation'
$info.Environment['paprika__DataDirectory'] = $data
foreach ($key in @('paprika__AzureKey', 'paprika__JevApiKey')) { $null = $info.Environment.Remove($key) }
$api = $null
$previousUrl = $env:PAPRIKA_TEST_URL
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
    $env:PAPRIKA_TEST_URL = 'http://127.0.0.1:5080'
    Push-Location "$root/tests"
    try {
        if ($TestFiles) { & npm.cmd test -- @TestFiles } else { & npm.cmd test }
        if ($LASTEXITCODE -ne 0) { throw 'Published application acceptance tests failed.' }
    } finally { Pop-Location }
} finally {
    $env:PAPRIKA_TEST_URL = $previousUrl
    if ($null -ne $api) {
        if (-not $api.HasExited) { $api.Kill($true); $api.WaitForExit() }
        $api.Dispose()
    }
    # This directory was allocated exclusively for this run; never delete the user's application data.
    if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
}
