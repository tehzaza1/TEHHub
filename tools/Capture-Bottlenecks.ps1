[CmdletBinding()]
param(
    [ValidateRange(5, 110)][int]$Seconds = 30,
    [ValidateSet('Debug', 'Release')][string]$ExpectedBuild = 'Debug',
    [string]$Label = 'baseline',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/performance')
)
$ErrorActionPreference = 'Stop'
$baseUri = 'http://localhost:9877/api/diagnostics'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$samples = [Collections.Generic.List[object]]::new()
try {
    Invoke-RestMethod -Uri "$baseUri/capture-start" -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 10 | Out-Null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $lastTime = ''
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        Start-Sleep -Milliseconds 1000
        $sample = Invoke-RestMethod -Uri "$baseUri/bottleneck-snapshot" -TimeoutSec 10
        if (!$sample.processId) { continue }
        if ($sample.build -ne $ExpectedBuild) { throw "Expected $ExpectedBuild, but connected to $($sample.build) process $($sample.processId)." }
        if ("$($sample.whenUtc)" -ne $lastTime) { $samples.Add($sample); $lastTime = "$($sample.whenUtc)" }
    }
}
finally {
    try { Invoke-RestMethod -Uri "$baseUri/capture-stop" -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 10 | Out-Null }
    catch { Write-Warning 'Unable to send stop; capture automatically expires after 120 seconds of render activity.' }
}
if ($samples.Count -eq 0) { throw 'No samples published; verify that the new Debug overlay is rendering.' }
$result = [ordered]@{ label=$Label; capturedUtc=[DateTime]::UtcNow.ToString('o'); expectedBuild=$ExpectedBuild; samples=$samples.ToArray() }
$outputPath = Join-Path $OutputDirectory ("capture-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmss'))-$([guid]::NewGuid().ToString('N')).json")
[IO.File]::WriteAllText($outputPath, ($result | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
Write-Output "Saved $($samples.Count) samples: $outputPath"
$samples[$samples.Count - 1].topInclusiveScopes | Select-Object -First 12 name,avgFrameNanoseconds,p95CallNanoseconds,allocatedBytesPerCall | Format-Table -AutoSize
