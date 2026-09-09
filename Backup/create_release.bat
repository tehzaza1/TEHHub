@echo off
setlocal enabledelayedexpansion

REM ============================================================
REM  GameHelper2 release builder
REM  Usage: create_release.bat <commit-or-tag>
REM  - Checks out the given ref into .\release_build (git worktree)
REM  - Builds the solution in Release
REM  - Produces GameHelper2_<ref>.zip with a top-level GameHelper\ folder
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
endlocal
