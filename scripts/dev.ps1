param(
    [switch]$Install,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$bun = (Get-Command bun -CommandType Application -ErrorAction Stop | Select-Object -First 1).Path
$children = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Start-Child([string]$file, [string[]]$arguments, [string]$directory, [bool]$frontend = $false) {
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $file
    $info.WorkingDirectory = $directory
    $info.UseShellExecute = $false
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    if ($frontend) {
        # Provider credentials belong only in the backend process.
        $null = $info.Environment.Remove('paprika__AzureKey')
        $null = $info.Environment.Remove('paprika__JevApiKey')
    }
    $process = [System.Diagnostics.Process]::Start($info)
    $children.Add($process)
    return $process
}

try {
    if ($Install) {
        Push-Location "$root/client"
        try {
            & $bun install --frozen-lockfile
            if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed.' }
        } finally { Pop-Location }
        & dotnet restore "$root/server/paprika.Api.csproj"
        if ($LASTEXITCODE -ne 0) { throw 'Backend restore failed.' }
    }
    if (-not (Test-Path "$root/client/node_modules")) { throw 'Run with -Install on first use.' }
    $api = Start-Child 'dotnet' @('run', '--project', "$root/server/paprika.Api.csproj", '--no-launch-profile', '--urls', 'http://127.0.0.1:5080') $root
    $ui = Start-Child $bun @('--bun', 'run', 'start', '--host', '127.0.0.1') "$root/client" $true
    Write-Host 'paprika: http://127.0.0.1:4200 (allow the first build to finish). Ctrl+C stops both processes.'
    if (-not $NoBrowser) { Start-Process 'http://127.0.0.1:4200' }
    while (-not $api.HasExited -and -not $ui.HasExited) { Start-Sleep -Seconds 1 }
    if ($api.HasExited -and $api.ExitCode -ne 0) { throw "API exited with code $($api.ExitCode)." }
    if ($ui.HasExited -and $ui.ExitCode -ne 0) { throw "Frontend exited with code $($ui.ExitCode)." }
} finally {
    foreach ($child in $children) {
        if (-not $child.HasExited) { $child.Kill($true); $child.WaitForExit() }
        $child.Dispose()
    }
}
