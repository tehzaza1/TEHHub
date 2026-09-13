# Deploy-Debug.ps1
# Syncs Debug build to C:\Games\Hy-v Tool\DXPEOE\TEHhub without touching configs/
$ErrorActionPreference = 'Stop'
$sourceDir = "$PSScriptRoot\..\TEHhub\bin\Debug\net10.0-windows\win-x64"
$targetDir = "C:\Games\Hy-v Tool\DXPEOE\TEHhub"

if (-not (Test-Path $sourceDir)) {
    throw "Source directory does not exist: $sourceDir. Please build TEHhub Debug first."
}

if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

Write-Host "Syncing Debug build to $targetDir (excluding configs, ini, and logs)..." -ForegroundColor Cyan

# Use robocopy to mirror files and plugins while strictly excluding configs and ini
$robocopyArgs = @(
    $sourceDir,
    $targetDir,
    "/E",
    "/XD", "configs", "logs",
    "/XF", "*.ini", "*.log",
    "/NJH", "/NJS", "/NDL", "/NC", "/NS"
)

& robocopy @robocopyArgs
$exitCode = $LASTEXITCODE
# Robocopy exit codes 0-7 indicate success / files copied
if ($exitCode -le 7) {
    Write-Host "[OK] Debug build deployed to $targetDir successfully." -ForegroundColor Green
} else {
    throw "Robocopy failed with exit code $exitCode"
}
