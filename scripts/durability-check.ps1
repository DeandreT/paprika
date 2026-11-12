param([string]$PackageDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/publish-platform'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
if (-not (Test-Path -LiteralPath "$package/PaperworkClassifier.Api.dll")) { throw 'Publish the durable application first.' }
if (Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 5080 must be free; existing processes are never stopped.' }
$root = Join-Path $env:LOCALAPPDATA ('Temp/opencode/paperwork-durability-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$api = $null
$base = 'http://127.0.0.1:5080'
$headers = @{ 'X-PaperworkClassifier' = '1' }
function Stop-TestApi {
    if ($null -ne $script:api) {
        if (-not $script:api.HasExited) { $script:api.Kill($true); $script:api.WaitForExit() }
        $script:api.Dispose()
        $script:api = $null
    }
}
function Start-TestApi([string]$data) {
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.UseShellExecute = $false
    $info.WorkingDirectory = $package
    foreach ($arg in @("$package/PaperworkClassifier.Api.dll", '--contentRoot', $package, '--Logging:LogLevel:Default=Error')) { $info.ArgumentList.Add($arg) }
    $info.Environment['Paperwork__Mode'] = 'Simulation'
    $info.Environment['Paperwork__DataDirectory'] = $data
    foreach ($key in @('Paperwork__AzureKey', 'Paperwork__JevApiKey')) { $null = $info.Environment.Remove($key) }
    $script:api = [Diagnostics.Process]::Start($info)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    while ([DateTimeOffset]::UtcNow -lt $deadline -and -not $script:api.HasExited) {
        try {
            if ((Invoke-RestMethod "$base/api/health" -TimeoutSec 2).status -eq 'ok') {
                $config = Invoke-RestMethod "$base/api/config"
                if ($config.mode -ne 'simulation' -or $config.ocrConfigured -or $config.classifierConfigured) { throw 'Verification requires simulation without credentials.' }
                return
            }
        } catch { }
        Start-Sleep -Milliseconds 100
    }
    throw 'Durable API did not become ready.'
}
function Wait-For([scriptblock]$predicate, [string]$reason, [int]$seconds = 60) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($seconds)
    do {
        if (& $predicate) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Timed out: $reason"
}
function Send([string]$path, $value) {
    Invoke-RestMethod "$base$path" -Method Post -Headers $headers -ContentType 'application/json' -Body ($value | ConvertTo-Json -Depth 40)
}
try {
    Start-TestApi "$root/original"
    $catalog = Invoke-RestMethod "$base/api/workflows"
    $baseline = @($catalog.entries | Where-Object { $_.kind -eq 'workflow' -and $_.id -eq $catalog.defaultWorkflow.id -and $_.version -eq $catalog.defaultWorkflow.version })[0]
    $draft = Send "/api/workflows/catalog/workflow/$($baseline.id)/$($baseline.version)/clone" @{ revision = $baseline.revision; name = 'Synthetic recovery workflow' }
    $draft.workflow.defaultSplit = @{ mode = 'separate'; pageCount = 1; pattern = '' }
    $draft = Invoke-RestMethod "$base/api/workflows/catalog/workflow/$($draft.id)/$($draft.version)" -Method Put -Headers $headers -ContentType 'application/json' -Body ($draft | ConvertTo-Json -Depth 40)
    $versionOne = Send "/api/workflows/catalog/workflow/$($draft.id)/$($draft.version)/publish" @{ revision = $draft.revision }
    $created = Send '/api/packets/demo' @{}
    Wait-For { (Invoke-RestMethod "$base/api/packets/$($created.id)").status -eq 'review' } 'demo processing'
    $packet = Invoke-RestMethod "$base/api/packets/$($created.id)"
    $created = Send "/api/packets/$($packet.id)/reprocess" @{ revision = $packet.revision; workflowId = $versionOne.id; workflowVersion = $versionOne.version }
    Wait-For { (Invoke-RestMethod "$base/api/packets/$($created.id)").status -eq 'review' } 'workflow processing'
    $packet = Invoke-RestMethod "$base/api/packets/$($created.id)"
    if ($packet.workflow.id -ne $versionOne.id -or $packet.workflow.version -ne $versionOne.version -or $packet.documents.Count -ne $packet.pages.Count) { throw 'The selected workflow was not applied.' }
    $snapshot = $packet.workflow | ConvertTo-Json -Depth 40 -Compress
    $body = @{ revision = $packet.revision; documents = $packet.documents; approve = $true } | ConvertTo-Json -Depth 40
    $packet = Invoke-RestMethod "$base/api/packets/$($packet.id)/review" -Method Put -Headers $headers -ContentType 'application/json' -Body $body
    $history = Invoke-RestMethod "$base/api/packets/$($packet.id)/history" | ConvertTo-Json -Depth 40 -Compress
    $next = Send "/api/workflows/catalog/workflow/$($versionOne.id)/$($versionOne.version)/versions" @{ revision = $versionOne.revision }
    $next.workflow.defaultSplit = @{ mode = 'consecutive'; pageCount = 1; pattern = '' }
    $next = Invoke-RestMethod "$base/api/workflows/catalog/workflow/$($next.id)/$($next.version)" -Method Put -Headers $headers -ContentType 'application/json' -Body ($next | ConvertTo-Json -Depth 40)
    $versionTwo = Send "/api/workflows/catalog/workflow/$($next.id)/$($next.version)/publish" @{ revision = $next.revision }
    $null = Invoke-RestMethod "$base/api/workflows/default" -Method Put -Headers $headers -ContentType 'application/json' -Body (@{ id = $versionTwo.id; version = $versionTwo.version } | ConvertTo-Json)
    $null = Send "/api/workflows/catalog/workflow/$($versionOne.id)/$($versionOne.version)/archive" @{ revision = $versionOne.revision }
    $catalogBefore = Invoke-RestMethod "$base/api/workflows"
    $entriesBefore = $catalogBefore.entries | ConvertTo-Json -Depth 40 -Compress
    $delivery = Send "/api/packets/$($packet.id)/deliveries" @{ revision = $packet.revision; scenario = 'transient-always' }
    # Catch a real persisted retry before it exhausts. Do not fabricate queue rows.
    Wait-For {
        $script:beforeCrash = @(Invoke-RestMethod "$base/api/packets/$($packet.id)/deliveries")[0]
        $state = Invoke-RestMethod "$base/api/messaging"
        $script:beforeCrash.attempts -ge 1 -and $script:beforeCrash.status -ne 'failed' -and
            @($state.queues | Where-Object { $_.name -eq 'delivery' -and $_.pending -gt 0 }).Count -gt 0
    } 'a persisted pending delivery retry' 30
    Stop-TestApi
    # The API observation can race with retry completion. Assert actual stopped state.
    $probeJson = & python "$PSScriptRoot/probe-pending-retry.py" "$root/original/jobs.sqlite" $delivery.id
    if ($LASTEXITCODE -ne 0) { throw 'Stopped-state retry proof failed; no recovery success is claimed.' }
    $stopped = $probeJson | ConvertFrom-Json
    & "$PSScriptRoot/backup.ps1" -DataDirectory "$root/original" -OutputDirectory "$root/backup"
    & "$PSScriptRoot/restore.ps1" -BackupDirectory "$root/backup" -DataDirectory "$root/restored"
    Remove-Item -LiteralPath "$root/original" -Recurse -Force
    Start-TestApi "$root/restored"
    $catalogAfter = Invoke-RestMethod "$base/api/workflows"
    if (($catalogAfter.entries | ConvertTo-Json -Depth 40 -Compress) -ne $entriesBefore -or $catalogAfter.defaultWorkflow.id -ne $versionTwo.id -or $catalogAfter.defaultWorkflow.version -ne $versionTwo.version) { throw 'Workflow catalog or default did not recover exactly.' }
    if (((Invoke-RestMethod "$base/api/packets/$($packet.id)").workflow | ConvertTo-Json -Depth 40 -Compress) -ne $snapshot) { throw 'Restore changed the pinned workflow.' }
    Wait-For {
        $script:letters = @((Invoke-RestMethod "$base/api/messaging").deadLetters | Where-Object deliveryId -EQ $delivery.id)
        $script:letters.Count -eq 1
    } 'retry exhaustion after restart'
    $failed = @(Invoke-RestMethod "$base/api/packets/$($packet.id)/deliveries")[0]
    if ($failed.status -ne 'failed' -or $failed.attempts -le $stopped.attempts) { throw 'Pending retries did not recover and exhaust.' }
    $letterId = $letters[0].id
    Stop-TestApi
    Start-TestApi "$root/restored"
    $persisted = @((Invoke-RestMethod "$base/api/messaging").deadLetters | Where-Object id -EQ $letterId)
    if ($persisted.Count -ne 1) { throw 'Dead letter did not survive another restart.' }
    $replayed = Send "/api/messaging/deadletters/$letterId/replay" @{}
    if ($replayed.status -ne 'queued') { throw 'Replay was not accepted.' }
    Wait-For {
        $current = @(Invoke-RestMethod "$base/api/packets/$($packet.id)/deliveries")[0]
        $dead = @((Invoke-RestMethod "$base/api/messaging").deadLetters | Where-Object deliveryId -EQ $delivery.id)
        $current.attempts -gt $failed.attempts -and $current.status -eq 'failed' -and $dead.Count -eq 1
    } 'replayed delivery retry exhaustion'
    if ((Invoke-RestMethod "$base/api/packets/$($packet.id)/history" | ConvertTo-Json -Depth 40 -Compress) -ne $history) { throw 'Retries/replay modified approved review history.' }
    if (((Invoke-RestMethod "$base/api/packets/$($packet.id)").workflow | ConvertTo-Json -Depth 40 -Compress) -ne $snapshot) { throw 'Replay changed the pinned workflow.' }
    $rerun = Send "/api/packets/$($packet.id)/reprocess" @{ revision = $packet.revision }
    Wait-For { (Invoke-RestMethod "$base/api/packets/$($rerun.id)").status -eq 'review' } 'reprocessing with archived pinned workflow'
    $rerun = Invoke-RestMethod "$base/api/packets/$($rerun.id)"
    if (($rerun.workflow | ConvertTo-Json -Depth 40 -Compress) -ne $snapshot -or $rerun.documents.Count -ne $rerun.pages.Count) { throw 'Reprocessing re-resolved the default instead of retaining the original workflow.' }
    Write-Host 'PASS: pending retry survived forced termination and backup/restore; catalog/default recovered; dead-letter replay and linked reprocessing retained the archived workflow snapshot and approved history.'
} finally {
    Stop-TestApi
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
