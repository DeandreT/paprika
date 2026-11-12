param(
    [string]$PackageDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/publish-platform'),
    [string]$SeedPackageDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
if (-not (Test-Path -LiteralPath "$package/PaperworkClassifier.Api.dll")) { throw 'Publish the application first.' }
$seedPackage = if ($SeedPackageDirectory) { (Resolve-Path -LiteralPath $SeedPackageDirectory).Path } else { $package }
if (-not (Test-Path -LiteralPath "$seedPackage/PaperworkClassifier.Api.dll")) { throw 'Seed package is missing the application.' }
if (Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue) { throw 'Recovery verification requires free port 5080; existing processes are never stopped.' }
$root = Join-Path $env:LOCALAPPDATA ('Temp/opencode/paperwork-recovery-' + [Guid]::NewGuid().ToString('N'))
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
function Start-TestApi([string]$data, [string]$runtimePackage = $package) {
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.UseShellExecute = $false
    $info.WorkingDirectory = $runtimePackage
    foreach ($arg in @("$runtimePackage/PaperworkClassifier.Api.dll", '--contentRoot', $runtimePackage, '--Logging:LogLevel:Default=Error')) { $info.ArgumentList.Add($arg) }
    $info.Environment['Paperwork__Mode'] = 'Simulation'
    $info.Environment['Paperwork__DataDirectory'] = $data
    foreach ($key in @('Paperwork__AzureKey', 'Paperwork__JevApiKey')) { $null = $info.Environment.Remove($key) }
    $script:api = [Diagnostics.Process]::Start($info)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ([DateTimeOffset]::UtcNow -lt $deadline -and -not $script:api.HasExited) {
        try {
            if ((Invoke-RestMethod "$base/api/health" -TimeoutSec 2).status -eq 'ok') {
                $config = Invoke-RestMethod "$base/api/config"
                if ($config.mode -ne 'simulation' -or $config.ocrConfigured -or $config.classifierConfigured) { throw 'Recovery must run without live providers.' }
                return
            }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    throw 'Isolated recovery API did not become ready without provider credentials.'
}
function Wait-Status([string]$url, [string]$status, [switch]$Delivery) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $value = Invoke-RestMethod $url
        if ($Delivery) { $value = @($value)[0] }
        if ($value.status -eq $status) { return $value }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Timed out waiting for $status."
}
function Download-Hash([string]$url, [string]$file) {
    Invoke-WebRequest $url -OutFile $file
    return (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
}
function Assert-PreservedJson([string]$beforeText, [string]$afterText, [string]$reason, [switch]$AllowMissingWorkflow) {
    $before = ConvertFrom-Json -InputObject $beforeText -NoEnumerate
    $after = ConvertFrom-Json -InputObject $afterText -NoEnumerate
    $a = @($before)
    $b = @($after)
    if ($a.Count -ne $b.Count) { throw "$reason changed item count." }
    for ($i = 0; $i -lt $a.Count; $i++) {
        if (-not ($a[$i].PSObject.Properties.Name -contains 'workflow')) {
            # New public metadata is additive; all pre-existing fields must still match.
            if (-not ($b[$i].PSObject.Properties.Name -contains 'workflow') -or
                -not ($b[$i].PSObject.Properties.Name -contains 'testRun') -or
                $b[$i].testRun -ne $false -or
                ($null -eq $b[$i].workflow -and -not $AllowMissingWorkflow) -or
                ($null -ne $b[$i].workflow -and ($b[$i].workflow.hash -notmatch '^[A-Fa-f0-9]{64}$' -or
                $b[$i].workflow.version -lt 1 -or $b[$i].workflow.releaseVersion -lt 1))) {
                throw "$reason has invalid additive workflow provenance."
            }
            $b[$i].PSObject.Properties.Remove('workflow')
            $b[$i].PSObject.Properties.Remove('testRun')
        }
        $oldNode = [System.Text.Json.Nodes.JsonNode]::Parse(($a[$i] | ConvertTo-Json -Depth 60 -Compress))
        $newNode = [System.Text.Json.Nodes.JsonNode]::Parse(($b[$i] | ConvertTo-Json -Depth 60 -Compress))
        if (-not [System.Text.Json.Nodes.JsonNode]::DeepEquals($oldNode, $newNode)) { throw "$reason changed existing fields." }
    }
}
function Assert-Rejected([string]$backup, [string]$target, [string]$reason, [string]$expectedError) {
    $rejected = $false
    try { & "$PSScriptRoot/restore.ps1" -BackupDirectory $backup -DataDirectory $target } catch {
        if ($_.Exception.Message -ne $expectedError) { throw "$reason failed for the wrong reason: $($_.Exception.Message)" }
        $rejected = $true
    }
    if (-not $rejected -or (Test-Path -LiteralPath $target)) { throw "$reason was not rejected atomically." }
}
try {
    Start-TestApi "$root/original" $seedPackage
    $created = Invoke-RestMethod "$base/api/packets/demo" -Method Post -Headers $headers
    $packet = Wait-Status "$base/api/packets/$($created.id)" 'review'
    $packet.documents[0].businessReference = 'SYNTHETIC-RECOVERY'
    foreach ($approve in @($false, $true)) {
        $body = @{ revision = $packet.revision; documents = $packet.documents; approve = $approve } | ConvertTo-Json -Depth 40
        $packet = Invoke-RestMethod "$base/api/packets/$($packet.id)/review" -Method Put -Headers $headers -ContentType 'application/json' -Body $body
    }
    if ($packet.status -ne 'approved') { throw 'Fixture was not approved.' }
    $history = Invoke-RestMethod "$base/api/packets/$($packet.id)/history" | ConvertTo-Json -Depth 40 -Compress
    $originals = @{}
    foreach ($sourceId in ($packet.pages.sourceId | Sort-Object -Unique)) {
        $originals[$sourceId] = Download-Hash "$base/api/packets/$($packet.id)/sources/$sourceId" "$root/source-$sourceId"
    }
    $null = Download-Hash "$base/api/packets/$($packet.id)/export" "$root/export-before.zip"
    $body = @{ revision = $packet.revision; scenario = 'success' } | ConvertTo-Json
    $null = Invoke-RestMethod "$base/api/packets/$($packet.id)/deliveries" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
    $delivery = Wait-Status "$base/api/packets/$($packet.id)/deliveries" 'delivered' -Delivery
    $artifactHash = Download-Hash "$base$($delivery.artifactUrl)" "$root/delivery-before.zip"
    if ($artifactHash -ne $delivery.sha256) { throw 'Delivery hash differs from its recorded hash.' }
    Stop-TestApi
    & python "$PSScriptRoot/probe-legacy-records.py" capture "$root/original/jobs.sqlite" "$root/legacy-records.json"
    if ($LASTEXITCODE -ne 0) { throw 'Could not capture original immutable-record evidence.' }
    & "$PSScriptRoot/backup.ps1" -DataDirectory "$root/original" -OutputDirectory "$root/backup"
    & "$PSScriptRoot/restore.ps1" -BackupDirectory "$root/backup" -DataDirectory "$root/restored"
    # Delete the original tree so stale absolute paths cannot make a broken restore appear successful.
    Remove-Item -LiteralPath "$root/original" -Recurse -Force
    Start-TestApi "$root/restored"
    $recovered = Invoke-RestMethod "$base/api/packets/$($packet.id)"
    if ($recovered.status -ne 'approved' -or $recovered.revision -ne $packet.revision) { throw 'Approval/revision was not recovered.' }
    $historyAfter = Invoke-RestMethod "$base/api/packets/$($packet.id)/history" | ConvertTo-Json -Depth 40 -Compress
    Assert-PreservedJson $history $historyAfter 'Review history'
    foreach ($sourceId in ($recovered.pages.sourceId | Sort-Object -Unique)) {
        $hash = Download-Hash "$base/api/packets/$($packet.id)/sources/$sourceId" "$root/recovered-$sourceId"
        if ($hash -ne $originals[$sourceId]) { throw 'An original source changed.' }
    }
    $null = Download-Hash "$base/api/packets/$($packet.id)/export" "$root/export-after.zip"
    # PDF/ZIP generation timestamps may differ; compare provenance and require actual PDF entries.
    $beforeZip = [IO.Compression.ZipFile]::OpenRead("$root/export-before.zip")
    $afterZip = [IO.Compression.ZipFile]::OpenRead("$root/export-after.zip")
    try {
        $beforeNames = @($beforeZip.Entries.FullName | Sort-Object)
        $afterNames = @($afterZip.Entries.FullName | Sort-Object)
        if (($beforeNames -join '|') -ne ($afterNames -join '|') -or -not ($afterNames -match '\.pdf$')) { throw 'Recovered export is incomplete.' }
        foreach ($entry in $beforeZip.Entries | Where-Object FullName -Match '\.json$') {
            $a = [IO.StreamReader]::new($entry.Open())
            $b = [IO.StreamReader]::new($afterZip.GetEntry($entry.FullName).Open())
            try { Assert-PreservedJson $a.ReadToEnd() $b.ReadToEnd() 'Recovered export provenance' } finally { $a.Dispose(); $b.Dispose() }
        }
    } finally { $beforeZip.Dispose(); $afterZip.Dispose() }
    $deliveryAfter = @(Invoke-RestMethod "$base/api/packets/$($packet.id)/deliveries")[0]
    # Old delivery snapshots predate workflows; null honestly preserves their missing metadata.
    Assert-PreservedJson ($delivery | ConvertTo-Json -Depth 40 -Compress) ($deliveryAfter | ConvertTo-Json -Depth 40 -Compress) 'Delivery record' -AllowMissingWorkflow
    if ((Download-Hash "$base$($deliveryAfter.artifactUrl)" "$root/delivery-after.zip") -ne $artifactHash) { throw 'Delivery artifact changed.' }
    # A restored database must execute fresh handlers, not only serve archived artifacts.
    $body = @{ revision = $recovered.revision } | ConvertTo-Json
    $rerun = Invoke-RestMethod "$base/api/packets/$($recovered.id)/reprocess" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
    $rerun = Wait-Status "$base/api/packets/$($rerun.id)" 'review'
    if ($rerun.parentPacketId -ne $recovered.id -or $rerun.pages.Count -ne $recovered.pages.Count) { throw 'Processing did not resume correctly from recovered originals.' }
    $body = @{ revision = $rerun.revision; documents = $rerun.documents; approve = $true } | ConvertTo-Json -Depth 40
    $rerun = Invoke-RestMethod "$base/api/packets/$($rerun.id)/review" -Method Put -Headers $headers -ContentType 'application/json' -Body $body
    $body = @{ revision = $rerun.revision; scenario = 'success' } | ConvertTo-Json
    $null = Invoke-RestMethod "$base/api/packets/$($rerun.id)/deliveries" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
    $freshDelivery = Wait-Status "$base/api/packets/$($rerun.id)/deliveries" 'delivered' -Delivery
    if ((Download-Hash "$base$($freshDelivery.artifactUrl)" "$root/fresh-delivery.zip") -ne $freshDelivery.sha256) { throw 'Fresh delivery failed after recovery.' }
    Assert-PreservedJson $history (Invoke-RestMethod "$base/api/packets/$($recovered.id)/history" | ConvertTo-Json -Depth 40 -Compress) 'Approved parent history after fresh processing'
    Stop-TestApi
    # A second startup must not re-import stale legacy delivery rows or requeue completed work.
    Start-TestApi "$root/restored"
    $oldDeliveries = @(Invoke-RestMethod "$base/api/packets/$($recovered.id)/deliveries")
    $newDeliveries = @(Invoke-RestMethod "$base/api/packets/$($rerun.id)/deliveries")
    if ($oldDeliveries.Count -ne 1 -or $newDeliveries.Count -ne 1) { throw 'Another startup duplicated migrated/completed deliveries.' }
    Assert-PreservedJson ($delivery | ConvertTo-Json -Depth 40 -Compress) ($oldDeliveries[0] | ConvertTo-Json -Depth 40 -Compress) 'Migrated delivery after another startup' -AllowMissingWorkflow
    Assert-PreservedJson ($freshDelivery | ConvertTo-Json -Depth 40 -Compress) ($newDeliveries[0] | ConvertTo-Json -Depth 40 -Compress) 'Fresh delivery after another startup'
    Stop-TestApi
    Copy-Item -LiteralPath "$root/backup" -Destination "$root/tampered" -Recurse
    & python "$PSScriptRoot/probe-legacy-records.py" verify "$root/restored/jobs.sqlite" "$root/legacy-records.json"
    if ($LASTEXITCODE -ne 0) { throw 'An original packet, review revision or prediction artifact changed during migration/recovery.' }
    [IO.File]::AppendAllText("$root/tampered/data/jobs.sqlite", 'tampered')
    Assert-Rejected "$root/tampered" "$root/rejected-tamper" 'Tampered backup' 'Backup integrity validation failed.'
    Copy-Item -LiteralPath "$root/backup" -Destination "$root/path-tests" -Recurse
    # Traversal must fail path validation even when the outside file exists with a matching hash.
    [IO.File]::WriteAllText("$root/path-tests/escape", 'outside-data-sentinel')
    $sentinelHash = (Get-FileHash -LiteralPath "$root/path-tests/escape" -Algorithm SHA256).Hash
    $manifestText = Get-Content -LiteralPath "$root/backup/manifest.json" -Raw
    foreach ($badPath in @('../escape', '..\escape', '/absolute', 'C:\absolute', 'jobs.sqlite:stream', './jobs.sqlite', 'folder//file', 'NUL', 'folder./file')) {
        $manifest = $manifestText | ConvertFrom-Json
        $manifest.files[0].path = $badPath
        if ($badPath -in @('../escape', '..\escape')) {
            $manifest.files[0].length = (Get-Item -LiteralPath "$root/path-tests/escape").Length
            $manifest.files[0].sha256 = $sentinelHash
        }
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$root/path-tests/manifest.json" -Encoding utf8NoBOM
        Assert-Rejected "$root/path-tests" "$root/rejected-path" "Unsafe path $badPath" 'Invalid path in backup manifest.'
    }
    if ((Get-FileHash -LiteralPath "$root/path-tests/escape" -Algorithm SHA256).Hash -ne $sentinelHash) { throw 'Traversal sentinel changed.' }
    Write-Host "PASS: approved packet, $($originals.Count) original source hashes, review history, export provenance, delivery record/artifact recovered with original data deleted; fresh processing and delivery succeeded; tampering and 9 unsafe manifest paths rejected."
} finally {
    Stop-TestApi
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
