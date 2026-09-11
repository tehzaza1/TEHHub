@echo off
setlocal enabledelayedexpansion

REM ============================================================
REM  GameHelper2 release builder
REM  Usage: create_release.bat <commit-or-tag>
REM  - Checks out the given ref into .\release_build (git worktree)
REM  - Builds the solution in Release
REM  - Publishes Launcher as a self-contained single-file win-x64 application
REM  - Excludes developer-only PDB symbols from the public package
REM  - Produces GameHelper2_<ref>.zip with a top-level GameHelper\ folder
REM  - GameHelper remains framework-dependent and requires .NET 10 Runtime x64
REM ============================================================

if "%~1"=="" (
    echo ERROR: No commit/tag specified.
    echo Usage: %~nx0 ^<commit-or-tag^>
    exit /b 1
)

set "REF=%~1"
set "ROOT=%~dp0"
REM Strip trailing backslash so quoted paths don't end in \" (which escapes the quote)
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "WORKTREE=%ROOT%\release_build"
REM Make the ref safe for use in a filename (replace / with _)
set "SAFEREF=%REF:/=_%"
set "ZIPNAME=GameHelper2_%SAFEREF%.zip"
set "ZIPPATH=%ROOT%\%ZIPNAME%"

echo === Preparing clean worktree for ref "%REF%" ===

REM Remove any previous worktree/folder
if exist "%WORKTREE%" (
    git -C "%ROOT%" worktree remove --force "%WORKTREE%" >nul 2>&1
    if exist "%WORKTREE%" rmdir /s /q "%WORKTREE%"
)

git -C "%ROOT%" worktree add --detach "%WORKTREE%" "%REF%"
if errorlevel 1 (
    echo ERROR: Failed to check out "%REF%".
    exit /b 1
)

echo === Building solution (Release) ===
pushd "%WORKTREE%"
dotnet build GameOverlay.sln -c Release
if errorlevel 1 (
    echo ERROR: Build failed.
    popd
    git -C "%ROOT%" worktree remove --force "%WORKTREE%" >nul 2>&1
    exit /b 1
)
popd

set "BUILDOUT=%WORKTREE%\GameHelper\bin\Release\net10.0-windows\win-x64"
if not exist "%BUILDOUT%" (
    echo ERROR: Build output not found at "%BUILDOUT%".
    git -C "%ROOT%" worktree remove --force "%WORKTREE%" >nul 2>&1
    exit /b 1
)

echo === Publishing standalone Launcher.exe ===
set "LAUNCHERPUBLISH=%WORKTREE%\_launcher_publish"
if exist "%LAUNCHERPUBLISH%" rmdir /s /q "%LAUNCHERPUBLISH%"

pushd "%WORKTREE%"
dotnet restore Launcher\Launcher.csproj -r win-x64 -p:SelfContained=true
if errorlevel 1 (
    echo ERROR: Failed to restore the standalone Launcher runtime pack.
    popd
    git -C "%ROOT%" worktree remove --force "%WORKTREE%" >nul 2>&1
    exit /b 1
)

dotnet publish Launcher\Launcher.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugSymbols=false -p:DebugType=None -o "%LAUNCHERPUBLISH%"
if errorlevel 1 (
    echo ERROR: Failed to publish standalone Launcher.exe.
    popd
    git -C "%ROOT%" worktree remove --force "%WORKTREE%" >nul 2>&1
    exit /b 1
)
popd

if not exist "%LAUNCHERPUBLISH%\Launcher.exe" (
    echo ERROR: Standalone Launcher.exe was not produced.
    git -C "%ROOT%" worktree remove --force "%WORKTREE%" >nul 2>&1
    exit /b 1
)

echo === Packaging "%ZIPNAME%" ===
if exist "%ZIPPATH%" del /q "%ZIPPATH%"

REM Stage the output under a folder named "GameHelper" so the zip has that as its top-level folder
set "STAGE=%WORKTREE%\_stage"
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%\GameHelper"

powershell -NoProfile -Command "Copy-Item -Path '%BUILDOUT%\*' -Destination '%STAGE%\GameHelper' -Recurse -Force"
if errorlevel 1 (
    echo ERROR: Failed to stage files.
    git -C "%ROOT%" worktree remove --force "%WORKTREE%" >nul 2>&1
    exit /b 1
)

REM Keep symbols in local build output for debugging, but do not ship them to users.
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%STAGE%\GameHelper' -Filter '*.pdb' -File -Recurse | Remove-Item -Force; if (Get-ChildItem -LiteralPath '%STAGE%\GameHelper' -Filter '*.pdb' -File -Recurse) { throw 'One or more PDB files remain in the release stage.' }"
if errorlevel 1 (
    echo ERROR: Failed to remove debug symbols from the release package.
    git -C "%ROOT%" worktree remove --force "%WORKTREE%" >nul 2>&1
    exit /b 1
)

REM Replace the framework-dependent launcher and its private dependencies with the standalone file.
powershell -NoProfile -Command "Remove-Item -LiteralPath '%STAGE%\GameHelper\Launcher.dll','%STAGE%\GameHelper\Launcher.deps.json','%STAGE%\GameHelper\Launcher.runtimeconfig.json' -Force -ErrorAction SilentlyContinue; Remove-Item -Path '%STAGE%\GameHelper\AsmResolver*.dll' -Force -ErrorAction SilentlyContinue; Copy-Item -LiteralPath '%LAUNCHERPUBLISH%\Launcher.exe' -Destination '%STAGE%\GameHelper\Launcher.exe' -Force"
if errorlevel 1 (
    echo ERROR: Failed to install standalone Launcher.exe in the package.
    git -C "%ROOT%" worktree remove --force "%WORKTREE%" >nul 2>&1
    exit /b 1
)

REM Keep the runtime prerequisite visible when users first open the package.
> "%STAGE%\GameHelper\README_FIRST.txt" (
    echo IMPORTANT: GameHelper2 requires Microsoft .NET 10 Runtime for Windows x64.
    echo Download: https://dotnet.microsoft.com/download/dotnet/10.0/runtime
    echo Install the runtime, then run Launcher.exe.
    echo The .NET SDK is required only when compiling the source code.
)

powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\GameHelper' -DestinationPath '%ZIPPATH%' -Force"
if errorlevel 1 (
    echo ERROR: Failed to create zip.
    git -C "%ROOT%" worktree remove --force "%WORKTREE%" >nul 2>&1
    exit /b 1
)

echo === Cleaning up worktree ===
git -C "%ROOT%" worktree remove --force "%WORKTREE%" >nul 2>&1
if exist "%WORKTREE%" rmdir /s /q "%WORKTREE%"

echo.
echo === DONE ===
echo Created: %ZIPPATH%
echo NOTE: Users must install Microsoft .NET 10 Runtime for Windows x64.
endlocal
