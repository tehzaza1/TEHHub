param([string]$Ref = 'HEAD')
$ErrorActionPreference = 'Stop'
$releaseRoot = Split-Path $PSScriptRoot -Parent
function Invoke-ReleaseCommand([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
}
function Get-FileSha256([string]$FilePath) {
    $stream = [IO.File]::OpenRead($FilePath)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $hasher.Dispose() }
}
$commit = (& git -C $releaseRoot rev-parse --verify --end-of-options "${Ref}^{commit}")
if ($LASTEXITCODE -ne 0) { throw 'Invalid release revision.' }
$commit = $commit.Trim()
$artifactRoot = Join-Path $releaseRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$worktree = Join-Path $artifactRoot ('.release-' + [guid]::NewGuid().ToString('N'))
$created = $false
try {
    Invoke-ReleaseCommand git @('-C', $releaseRoot, 'worktree', 'add', '--detach', $worktree, $commit)
    $created = $true
    $solution = Join-Path $worktree 'TEHhub.sln'
    if (!(Test-Path -LiteralPath $solution)) { throw 'This revision is not a TEHhub release.' }
    Invoke-ReleaseCommand dotnet @('build', $solution, '-c', 'Release', '--nologo')
    $publish = Join-Path $worktree '_launcher_publish'
    Invoke-ReleaseCommand dotnet @('publish', (Join-Path $worktree 'TEHhub.Launcher/TEHhub.Launcher.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:PublishTrimmed=false', '-p:DebugSymbols=false', '-p:DebugType=None', '-o', $publish)
    $buildOutput = Join-Path $worktree 'TEHhub/bin/Release/net10.0-windows/win-x64'
    $stage = Join-Path $worktree '_stage/TEHhub'
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $buildOutput -File -Recurse) {
        $relative = $file.FullName.Substring($buildOutput.Length + 1)
        if ($file.Extension -eq '.pdb' -or $relative -match '^(configs|logs|entity_dumps|offset-recovery|tools)[\\/]' -or $relative -match '^(Start|Stop)-PerformanceMonitor\.bat$' -or $relative -match '^(TEHhub\.Launcher\.(exe|dll|deps\.json|runtimeconfig\.json)|AsmResolver.*\.dll)$') { continue }
        $destination = Join-Path $stage $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    Copy-Item -LiteralPath (Join-Path $publish 'TEHhub.Launcher.exe') -Destination $stage
    foreach ($name in @('README.md', 'CHANGELOG.md')) { Copy-Item -LiteralPath (Join-Path $worktree $name) -Destination $stage }
    Copy-Item -LiteralPath (Join-Path $worktree 'Documentation') -Destination $stage -Recurse
    if (Test-Path -LiteralPath (Join-Path $worktree 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $worktree 'LICENSE') -Destination $stage }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $worktree 'Plugins') -File -Recurse | Where-Object { $_.Name -in @('LICENSE', 'CREDITS.md') -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }) {
        $destination = Join-Path $stage $file.FullName.Substring($worktree.Length + 1)
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $stage 'README_FIRST.txt'), "TEHhub requires Microsoft .NET 10 Runtime for Windows x64.`r`nhttps://dotnet.microsoft.com/download/dotnet/10.0/runtime`r`nRun TEHhub.Launcher.exe after installing the runtime.`r`nPlugin settings: configs/plugins. Legacy GameHelper2 plugin DLLs must be rebuilt against TEHhub.`r`n", $utf8)
    [xml]$project = Get-Content -LiteralPath (Join-Path $worktree 'TEHhub/TEHhub.csproj')
    $version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    $manifest = [ordered]@{ product = 'TEHhub'; version = "$version"; commit = $commit; target = 'win-x64'; runtime = '.NET 10 Runtime x64'; builtUtc = [DateTime]::UtcNow.ToString('o'); launcher = 'self-contained single-file' }
    [IO.File]::WriteAllText((Join-Path $stage 'RELEASE_MANIFEST.json'), ($manifest | ConvertTo-Json), $utf8)
    foreach ($name in @('TEHhub.exe', 'TEHhub.dll', 'TEHhub.Offsets.dll', 'TEHhub.runtimeconfig.json', 'TEHhub.Launcher.exe', 'README.md', 'CHANGELOG.md', 'README_FIRST.txt', 'RELEASE_MANIFEST.json', 'resources')) {
        if (!(Test-Path -LiteralPath (Join-Path $stage $name))) { throw "Missing package file or directory: $name" }
    }
    if (Get-ChildItem -LiteralPath $stage -Filter '*.pdb' -Recurse -File) { throw 'Debug symbols must not be shipped.' }
    $coreBytes = [IO.File]::ReadAllBytes((Join-Path $stage 'TEHhub.dll'))
    $coreMetadata = [Text.Encoding]::UTF8.GetString($coreBytes)
    foreach ($typeName in @('LocalDiagnosticsApi', 'ToolHubSnapshot', 'ToolDiagnostics')) {
        if ($coreMetadata.Contains($typeName)) { throw "Debug API type found in Release: $typeName" }
    }
    foreach ($mappingName in @('pathBasenameToItemName.json', 'uniqueArtMapping.json')) {
        $sourceMapping = Join-Path $worktree "Plugins/NinjaPricer/$mappingName"
        $packagedMapping = Join-Path $stage "Plugins/NinjaPricer/$mappingName"
        if (!(Test-Path -LiteralPath $packagedMapping) -or
            (Get-FileSha256 $sourceMapping) -ne (Get-FileSha256 $packagedMapping)) {
            throw "Missing or outdated NinjaPricer mapping: $mappingName"
        }
    }
    if (!(Get-ChildItem -LiteralPath (Join-Path $stage 'Plugins') -Filter '*.dll' -Recurse -File)) { throw 'No plugins were packaged.' }
    $launcherCheck = Start-Process -FilePath (Join-Path $stage 'TEHhub.Launcher.exe') -ArgumentList '--check' -WindowStyle Hidden -Wait -PassThru
    if ($launcherCheck.ExitCode -ne 0) { throw "Launcher check failed with exit code $($launcherCheck.ExitCode)" }
    $zipPath = Join-Path $artifactRoot "TEHhub-v$version-$($commit.Substring(0,8))-win-x64.zip"
    if (Test-Path -LiteralPath $zipPath) { throw "Release already exists: $zipPath" }
    Compress-Archive -LiteralPath $stage -DestinationPath $zipPath
    $stream = [IO.File]::OpenRead($zipPath)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha256.Dispose() }
    [IO.File]::WriteAllText(($zipPath + '.sha256'), "$hash  $([IO.Path]::GetFileName($zipPath))`r`n", $utf8)
    Write-Host "Created: $zipPath"
    Write-Host "SHA256: $hash"
}
finally {
    if ($created) {
        $checkedPath = [IO.Path]::GetFullPath($worktree)
        $boundary = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\') + '\'
        if (!$checkedPath.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path.' }
        & git -C $releaseRoot worktree remove --force $checkedPath
        if ($LASTEXITCODE -ne 0) { Write-Warning "Temporary worktree retained: $checkedPath" }
    }
}
