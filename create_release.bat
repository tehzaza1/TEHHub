@echo off
setlocal
if "%~1"=="" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Create-Release.ps1" -Ref HEAD
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Create-Release.ps1" -Ref "%~1"
)
exit /b %errorlevel%
