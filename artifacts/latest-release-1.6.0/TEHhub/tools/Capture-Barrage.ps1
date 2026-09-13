param([Parameter(Mandatory=$true)][ValidateRange(0,20)][int]$Remaining,
      [string]$OutputDirectory = "$PSScriptRoot\..\logs\skill-research")
$ErrorActionPreference = 'Stop'
$sample = Invoke-RestMethod 'http://localhost:9877/api/diagnostics/skill-research' -Method Post -TimeoutSec 8
if ($sample.values.Count -eq 0) { throw 'No cached player skill/buff data; check game attachment.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$report = @{ observedRemaining = $Remaining; snapshot = $sample }
$path = Join-Path $OutputDirectory ("barrage-{0}-{1}.json" -f $Remaining, [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
Write-Output $path
