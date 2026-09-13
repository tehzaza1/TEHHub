param(
    [Parameter(Mandatory=$false)]
    [ValidateRange(0, 16)]
    [int]$PlacedBombs = -1,

    [Parameter(Mandatory=$false)]
    [switch]$Reset,

    [Parameter(Mandatory=$false)]
    [switch]$GetCandidates,

    [Parameter(Mandatory=$false)]
    [switch]$CaptureRemnants,

    [Parameter(Mandatory=$false)]
    [string]$OutputDirectory = "$PSScriptRoot\..\artifacts"
)

$ErrorActionPreference = 'Stop'
$baseUrl = 'http://localhost:9877'

function Test-ApiReachable {
    try {
        $res = Invoke-WebRequest -Uri "$baseUrl/api/diagnostics/capture-status" -Method Get -TimeoutSec 3 -ErrorAction Stop
        return $true
    } catch {
        return $false
    }
}

if (-not (Test-ApiReachable)) {
    Write-Host "[ERROR] Cannot connect to TEHhub diagnostics API at $baseUrl." -ForegroundColor Red
    Write-Host "Please start TEHhub (Debug build) from: C:\Games\Hy-v Tool\DXPEOE\TEHhub" -ForegroundColor Yellow
    exit 1
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')

# Single action mode: Reset
if ($Reset) {
    Write-Host "[*] Resetting differential offset scanner..." -ForegroundColor Cyan
    $res = Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/expedition-offset-scan/reset" -Method Post -TimeoutSec 5
    Write-Host "[OK] $($res.message)" -ForegroundColor Green
    exit 0
}

# Single action mode: PlacedBombs
if ($PlacedBombs -ge 0) {
    Write-Host "[*] Sending placedBombs=$PlacedBombs..." -ForegroundColor Cyan
    $res = Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/expedition-offset-scan?placedBombs=$PlacedBombs" -Method Post -TimeoutSec 10
    $candCount = if ($res.candidates) { $res.candidates.Count } else { 0 }
    Write-Host "[+] Status: $($res.status), Observed: $($res.observedPlacedBombs), Surviving Candidates: $candCount" -ForegroundColor Green
    if ($candCount -gt 0 -and $candCount -le 20) {
        $res.candidates | Format-Table Structure, BaseAddress, Offset, Address, ValueSequence
    }
    exit 0
}

# Single action mode: GetCandidates
if ($GetCandidates) {
    Write-Host "[*] Fetching surviving candidates..." -ForegroundColor Cyan
    $res = Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/expedition-offset-scan" -Method Get -TimeoutSec 5
    $candCount = if ($res.candidates) { $res.candidates.Count } else { 0 }
    Write-Host "[+] Status: $($res.status), Surviving Candidates: $candCount" -ForegroundColor Green
    if ($candCount -gt 0) {
        $res.candidates | Format-Table Structure, BaseAddress, Offset, Address, ValueSequence
    }
    exit 0
}

# Single action mode: CaptureRemnants
if ($CaptureRemnants) {
    Write-Host "[*] Capturing Expedition probe & placement evidence..." -ForegroundColor Cyan
    $probe = Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/expedition-probe" -Method Post -TimeoutSec 10
    $placement = Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/expedition-placement-probe" -Method Post -TimeoutSec 10
    
    $probeFile = Join-Path $OutputDirectory "expedition-probe-$timestamp.json"
    $placementFile = Join-Path $OutputDirectory "expedition-placement-probe-$timestamp.json"
    
    $probe | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $probeFile -Encoding UTF8
    $placement | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $placementFile -Encoding UTF8
    
    Write-Host "[OK] Saved probe to: $probeFile" -ForegroundColor Green
    Write-Host "[OK] Saved placement to: $placementFile" -ForegroundColor Green
    exit 0
}

# Interactive Wizard: 0 -> 1 -> 2 -> 3 -> 0
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "   Expedition Differential Offset Scanner (Wizard)     " -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "This sequence captures: 0 -> 1 -> 2 -> 3 -> 0 in a live Expedition.`n"

# Step 1: Reset & Scan 0
Write-Host "Step 1/5: Make sure you are in an active Expedition encounter with 0 bombs placed." -ForegroundColor Yellow
$null = Read-Host "Press [ENTER] to Reset and scan baseline (placedBombs=0)"
Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/expedition-offset-scan/reset" -Method Post -TimeoutSec 5 | Out-Null
$res0 = Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/expedition-offset-scan?placedBombs=0" -Method Post -TimeoutSec 10
$count0 = if ($res0.candidates) { $res0.candidates.Count } else { 0 }
Write-Host "[+] Baseline captured: $count0 initial candidates found.`n" -ForegroundColor Green

# Step 2: Place Bomb 1
Write-Host "Step 2/5: In PoE2, place BOMB 1 (do not detonate yet)." -ForegroundColor Yellow
$null = Read-Host "Press [ENTER] after Bomb 1 is placed (placedBombs=1)"
$res1 = Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/expedition-offset-scan?placedBombs=1" -Method Post -TimeoutSec 10
$count1 = if ($res1.candidates) { $res1.candidates.Count } else { 0 }
Write-Host "[+] Bomb 1 captured: $count1 surviving candidates.`n" -ForegroundColor Green

# Step 3: Place Bomb 2
Write-Host "Step 3/5: In PoE2, place BOMB 2." -ForegroundColor Yellow
$null = Read-Host "Press [ENTER] after Bomb 2 is placed (placedBombs=2)"
$res2 = Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/expedition-offset-scan?placedBombs=2" -Method Post -TimeoutSec 10
$count2 = if ($res2.candidates) { $res2.candidates.Count } else { 0 }
Write-Host "[+] Bomb 2 captured: $count2 surviving candidates.`n" -ForegroundColor Green

# Step 4: Place Bomb 3
Write-Host "Step 4/5: In PoE2, place BOMB 3." -ForegroundColor Yellow
$null = Read-Host "Press [ENTER] after Bomb 3 is placed (placedBombs=3)"
$res3 = Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/expedition-offset-scan?placedBombs=3" -Method Post -TimeoutSec 10
$count3 = if ($res3.candidates) { $res3.candidates.Count } else { 0 }
Write-Host "[+] Bomb 3 captured: $count3 surviving candidates.`n" -ForegroundColor Green

# Step 5: Undo / Clear all bombs
Write-Host "Step 5/5: In PoE2, undo/cancel all placed bombs back to 0 (or pick them up)." -ForegroundColor Yellow
$null = Read-Host "Press [ENTER] after all bombs are cleared back to 0 (placedBombs=0)"
$resFinal = Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/expedition-offset-scan?placedBombs=0" -Method Post -TimeoutSec 10
$countFinal = if ($resFinal.candidates) { $resFinal.candidates.Count } else { 0 }
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "[+] Full sequence completed! Surviving candidates: $countFinal" -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Cyan

if ($countFinal -gt 0) {
    $resFinal.candidates | Format-Table Structure, BaseAddress, Offset, Address, ValueSequence
} else {
    Write-Host "No candidate survived the full 0 -> 1 -> 2 -> 3 -> 0 sequence." -ForegroundColor Red
}

$savePath = Join-Path $OutputDirectory "expedition-offset-scan-$timestamp.json"
$resFinal | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $savePath -Encoding UTF8
Write-Host "Result saved to: $savePath" -ForegroundColor Cyan

# Also ask about remnant capture
$ans = Read-Host "`nCapture Remnant / Entity probe now as well? (Y/n)"
if ($ans -ne 'n' -and $ans -ne 'N') {
    Write-Host "[*] Capturing Expedition probe..." -ForegroundColor Cyan
    $probe = Invoke-RestMethod -Uri "$baseUrl/api/diagnostics/expedition-probe" -Method Post -TimeoutSec 10
    $probeFile = Join-Path $OutputDirectory "expedition-probe-$timestamp.json"
    $probe | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $probeFile -Encoding UTF8
    Write-Host "[OK] Saved Remnant probe to: $probeFile" -ForegroundColor Green
}
