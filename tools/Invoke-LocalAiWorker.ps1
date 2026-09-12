[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Task,

    [string[]]$ContextPath = @(),

    [string]$Model = 'qwen2.5-coder:7b-instruct-q4_K_M',

    [string]$OutputPath,

    [ValidateRange(1024, 32768)]
    [int]$ContextCharactersPerFile = 48000,

    [ValidateRange(4096, 262144)]
    [int]$ContextLength = 32768,

    [ValidateRange(4096, 1000000)]
    [int]$MaxTotalContextCharacters = 160000
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$context = [Collections.Generic.List[string]]::new()
$contextCharacterCount = 0

foreach ($path in $ContextPath) {
    $resolvedPath = [IO.Path]::GetFullPath($path)
    if (-not $resolvedPath.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Context file must be inside this repository: $path"
    }

    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Context file was not found: $path"
    }

    $content = Get-Content -LiteralPath $resolvedPath -Raw
    if ($content.Length -gt $ContextCharactersPerFile) {
        $content = $content.Substring(0, $ContextCharactersPerFile) + "`n... [truncated]"
    }

    $remainingCharacters = $MaxTotalContextCharacters - $contextCharacterCount
    if ($remainingCharacters -le 0) {
        break
    }

    if ($content.Length -gt $remainingCharacters) {
        $content = $content.Substring(0, $remainingCharacters) + "`n... [total context limit reached]"
    }

    $relativePath = [IO.Path]::GetRelativePath($repositoryRoot, $resolvedPath)
    $context.Add("FILE: $relativePath`n--- BEGIN FILE ---`n$content`n--- END FILE ---")
    $contextCharacterCount += $content.Length
}

$systemPrompt = @'
You are a local coding worker for a C#/.NET repository. Produce a small, reviewable answer for the stated task.
Do not claim that you ran commands or edited files. When a code change is requested, return a unified diff only.
Keep the change minimal, preserve public APIs unless the task explicitly changes them, and do not modify generated files, binaries, or secrets.
'@

$userPrompt = "TASK:`n$Task"
if ($context.Count -gt 0) {
    $userPrompt += "`n`nCONTEXT:`n" + ($context -join "`n`n")
}

$request = @{
    model = $Model
    messages = @(
        @{ role = 'system'; content = $systemPrompt }
        @{ role = 'user'; content = $userPrompt }
    )
    stream = $false
    options = @{ temperature = 0.1; num_ctx = $ContextLength }
} | ConvertTo-Json -Depth 8

try {
    $response = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/chat' -Method Post -ContentType 'application/json' -Body $request -TimeoutSec 300
}
catch {
    throw "Local Ollama worker is unavailable. Start it with: ollama serve. Details: $($_.Exception.Message)"
}

$answer = $response.message.content.Trim()
if ([string]::IsNullOrWhiteSpace($answer)) {
    throw 'The local worker returned an empty response.'
}

if ($answer -match '(?s)^```(?:diff|patch)?\s*\r?\n(.*)\r?\n```$') {
    $answer = $Matches[1].Trim()
}

if ($OutputPath) {
    $outputFullPath = [IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = Split-Path -Parent $outputFullPath
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    Set-Content -LiteralPath $outputFullPath -Value $answer -Encoding utf8NoBOM
}

$answer
