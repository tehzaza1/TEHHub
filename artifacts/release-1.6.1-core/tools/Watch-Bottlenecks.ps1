[CmdletBinding()]
param(
    [ValidateSet('Run','Start','Stop','Status')][string]$Mode = 'Run',
    [ValidateRange(5,60)][int]$CaptureSeconds = 20,
    [ValidateRange(90,3600)][int]$IntervalSeconds = 180,
    [ValidateSet('Any','Debug','Release')][string]$ExpectedBuild = 'Any',
    [ValidateSet('Area','Timed')][string]$TrackingMode = 'Area'
)
$ErrorActionPreference = 'Stop'
$monitorRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../logs/performance-monitor'))
$statePath = Join-Path $monitorRoot 'status.json'
$stopPath = Join-Path $monitorRoot 'stop.request'
New-Item -ItemType Directory -Path $monitorRoot -Force | Out-Null
if ($Mode -eq 'Stop') { [IO.File]::WriteAllText($stopPath, 'stop'); Write-Output 'Monitor stop requested.'; return }
if ($Mode -eq 'Status') { if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw }; return }
if ($Mode -eq 'Start') {
    $startArguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Mode Run -CaptureSeconds {1} -IntervalSeconds {2} -ExpectedBuild {3} -TrackingMode {4}' -f $PSCommandPath, $CaptureSeconds, $IntervalSeconds, $ExpectedBuild, $TrackingMode
    $backgroundMonitor = Start-Process powershell.exe -WindowStyle Hidden -ArgumentList $startArguments -PassThru
    Write-Output "Monitor started: PID $($backgroundMonitor.Id). Reports: $monitorRoot"
    return
}
$sha = [Security.Cryptography.SHA256]::Create()
try { $identity = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($monitorRoot.ToLowerInvariant()))).Replace('-','') } finally { $sha.Dispose() }
$mutex = [Threading.Mutex]::new($false, ('Local\TEHhub-PerformanceMonitor-' + $identity))
$acquired = $false
try { $acquired = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
if (!$acquired) { $mutex.Dispose(); return }
$baseUri = 'http://localhost:9877/api/diagnostics'
$utf8 = [Text.UTF8Encoding]::new($false)
$roundCount = 0
$ownedCapture = ''
$ownedPid = 0
$lastReportPath = ''
$ownsAreaMode = $false
function Save-State([string]$Stage, [string]$Message, [string]$Report = '') {
    if ($Report) { $script:lastReportPath = $Report }
    $state = @{ monitorPid=$PID; updatedUtc=[DateTime]::UtcNow.ToString('o'); stage=$Stage; message=$Message; completedRounds=$roundCount; lastReport=$lastReportPath; captureSeconds=$CaptureSeconds; intervalSeconds=$IntervalSeconds }
    $temporary = $statePath + '.tmp'
    [IO.File]::WriteAllText($temporary, ($state | ConvertTo-Json), $utf8)
    Move-Item -LiteralPath $temporary -Destination $statePath -Force
}
function Wait-Monitor([double]$Seconds) {
    $waitClock = [Diagnostics.Stopwatch]::StartNew()
    while ($waitClock.Elapsed.TotalSeconds -lt $Seconds) {
        if (Test-Path -LiteralPath $stopPath) { return $false }
        Start-Sleep -Milliseconds 500
    }
    return $true
}
function Stop-OwnedCapture {
    if (!$ownedCapture -and !$ownsAreaMode) { return }
    try {
        $currentStatus = Invoke-RestMethod "$baseUri/capture-status" -TimeoutSec 3
        if ($currentStatus.processId -eq $ownedPid -and (($ownsAreaMode -and $currentStatus.areaMode) -or ($currentStatus.captureId -eq $ownedCapture -and $currentStatus.active))) {
            Invoke-RestMethod "$baseUri/capture-stop" -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 3 | Out-Null
        }
    } catch { }
}
try {
    if (Test-Path -LiteralPath $stopPath) { Remove-Item -LiteralPath $stopPath }
    Save-State 'waiting' 'Waiting for TEHhub diagnostics API.'
    if ($TrackingMode -eq 'Area') {
        while (!(Test-Path -LiteralPath $stopPath)) {
            try {
                $areaStatus = Invoke-RestMethod "$baseUri/capture-status" -TimeoutSec 3
                if ($ExpectedBuild -ne 'Any' -and $areaStatus.build -ne $ExpectedBuild) { throw "Waiting for $ExpectedBuild." }
                if (!$areaStatus.areaMode) {
                    if ($areaStatus.active) { throw 'Another capture is active; waiting without resetting it.' }
                    Invoke-RestMethod "$baseUri/capture-area-start" -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 3 | Out-Null
                }
                $ownedPid = $areaStatus.processId
                $ownsAreaMode = $true
                if ($areaStatus.active) {
                    $areaSample = Invoke-RestMethod "$baseUri/bottleneck-snapshot" -TimeoutSec 3
                    if ($areaSample.processId -eq $ownedPid -and $areaSample.captureId -eq $areaStatus.captureId) {
                        [IO.File]::WriteAllText((Join-Path $monitorRoot 'current-map.json'), ($areaSample | ConvertTo-Json -Depth 16), $utf8)
                        Save-State 'capturing-map' "Tracking the whole area $($areaSample.areaHash)."
                    }
                } else { Save-State 'waiting-map' 'Town, hideout, loading or unknown area: not counting.' }
            } catch { Save-State 'waiting' $_.Exception.Message }
            if (!(Wait-Monitor 2)) { break }
        }
        return
    }
    while (!(Test-Path -LiteralPath $stopPath)) {
        $samples = [Collections.Generic.List[object]]::new()
        $ownedCapture = ''
        try {
            $preflight = Invoke-RestMethod "$baseUri/capture-status" -TimeoutSec 3
            if ($ExpectedBuild -ne 'Any' -and $preflight.build -ne $ExpectedBuild) { throw "Waiting for $ExpectedBuild build; current instance is left unchanged." }
            if ($preflight.active) { throw 'Another capture is active; waiting without resetting it.' }
            $ownedPid = $preflight.processId
            Invoke-RestMethod "$baseUri/capture-start" -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 3 | Out-Null
            $beginClock = [Diagnostics.Stopwatch]::StartNew()
            while ($beginClock.Elapsed.TotalSeconds -lt 8) {
                if (!(Wait-Monitor 0.5)) { break }
                $startedStatus = Invoke-RestMethod "$baseUri/capture-status" -TimeoutSec 3
                if ($startedStatus.processId -ne $ownedPid) { throw 'Overlay restarted while starting capture.' }
                if ($startedStatus.active -and $startedStatus.captureId -ne $preflight.captureId) { $ownedCapture = $startedStatus.captureId; break }
            }
            if (!$ownedCapture) { throw 'Capture did not begin; overlay may not be rendering.' }
            Save-State 'capturing' "Collecting $CaptureSeconds seconds from $($preflight.build) PID $ownedPid."
            $captureClock = [Diagnostics.Stopwatch]::StartNew()
            $lastTimestamp = ''
            while ($captureClock.Elapsed.TotalSeconds -lt $CaptureSeconds) {
                if (!(Wait-Monitor 2)) { break }
                $sample = Invoke-RestMethod "$baseUri/bottleneck-snapshot" -TimeoutSec 3
                if (!$sample.processId) { continue }
                if ($sample.processId -ne $ownedPid -or $sample.captureId -ne $ownedCapture) { throw 'Capture identity changed; not mixing sessions.' }
                if ("$($sample.whenUtc)" -ne $lastTimestamp) { $samples.Add($sample); $lastTimestamp = "$($sample.whenUtc)" }
            }
        }
        catch { Save-State 'waiting' $_.Exception.Message }
        finally { Stop-OwnedCapture }
        if ($samples.Count -gt 0) {
            $latest = $samples[$samples.Count-1]
            $summary = @{ source='Instrumented samples'; build=$latest.build; version=$latest.version; processId=$ownedPid; captureId=$ownedCapture; samples=$samples.Count; highestObservedP99RenderMs=($samples | Measure-Object p99RenderMilliseconds -Maximum).Maximum; latestWorkingSetMiB=$latest.workingSetBytes/1MB; latestMemoryFailures=$latest.memory.totalFailures; topInclusiveScopes=$latest.topInclusiveScopes }
            $reportPath = Join-Path $monitorRoot ("round-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmss'))-$([guid]::NewGuid().ToString('N')).json")
            [IO.File]::WriteAllText($reportPath, (@{ summary=$summary; samples=$samples.ToArray() } | ConvertTo-Json -Depth 16), $utf8)
            [IO.File]::WriteAllText((Join-Path $monitorRoot 'latest-summary.json'), ($summary | ConvertTo-Json -Depth 8), $utf8)
            $trendPath = Join-Path $monitorRoot 'trend-history.jsonl'
            if ((Test-Path -LiteralPath $trendPath) -and (Get-Item -LiteralPath $trendPath).Length -gt 5MB) {
                Move-Item -LiteralPath $trendPath -Destination (Join-Path $monitorRoot 'trend-history.previous.jsonl') -Force
            }
            $trend = @{ whenUtc=$latest.whenUtc; build=$latest.build; processId=$ownedPid; captureId=$ownedCapture; p99RenderMs=$summary.highestObservedP99RenderMs; workingSetMiB=$summary.latestWorkingSetMiB; privateBytes=$latest.privateBytes; managedBytes=$latest.managedBytes; memoryFailures=$summary.latestMemoryFailures }
            [IO.File]::AppendAllText($trendPath, ($trend | ConvertTo-Json -Compress) + [Environment]::NewLine, $utf8)
            $roundCount++
            foreach ($old in Get-ChildItem -LiteralPath $monitorRoot -Filter 'round-*.json' -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -Skip 30) { Remove-Item -LiteralPath $old.FullName }
            Save-State 'resting' 'Saved report; instrumentation is stopped between rounds.' $reportPath
            if (!(Wait-Monitor ([Math]::Max(1, $IntervalSeconds-$CaptureSeconds)))) { break }
        } else { if (!(Wait-Monitor 5)) { break } }
    }
}
finally {
    Stop-OwnedCapture
    Save-State 'stopped' 'Monitor stopped.'
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
