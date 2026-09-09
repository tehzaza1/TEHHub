@echo off
setlocal enabledelayedexpansion

REM Pull all plugin folders that are their own git repos (i.e. not part of the
REM main GameHelper2 repository). A folder is considered "external" if it
REM contains its own .git directory/file. Nothing is hardcoded.

REM Work relative to the location of this script (repo root); operate on Plugins\.
set "PLUGINS_DIR=%~dp0Plugins"
cd /d "%PLUGINS_DIR%"

set "REPORT="
set "COUNT=0"

echo.
echo === Pulling external plugin repositories ===
echo.

for /d %%D in (*) do (
    if exist "%%D\.git" (
        set /a COUNT+=1
        echo --------------------------------------------------
        echo [%%D] pulling...
        pushd "%%D"

        REM Capture HEAD before and after to detect whether anything changed.
        for /f "delims=" %%H in ('git rev-parse HEAD 2^>nul') do set "BEFORE=%%H"

        git pull
        set "PULL_RC=!ERRORLEVEL!"

        for /f "delims=" %%H in ('git rev-parse HEAD 2^>nul') do set "AFTER=%%H"

        popd

        if "!PULL_RC!"=="0" (
            if "!BEFORE!"=="!AFTER!" (
                set "REPORT=!REPORT!  [ OK   ] %%D - up to date (no changes)#"
            ) else (
                set "REPORT=!REPORT!  [UPDATED] %%D - new commits pulled#"
            )
        ) else (
            set "REPORT=!REPORT!  [ FAIL ] %%D - git pull failed (exit !PULL_RC!)#"
        )
    )
)

echo.
echo ==================================================
echo                    REPORT
echo ==================================================
if "%COUNT%"=="0" (
    echo No external plugin repositories found.
) else (
    call :print_report
)
echo ==================================================
echo.

REM Keep the window open when double-clicked so the report stays visible.
echo Press any key to close this window . . .
pause >nul
endlocal
exit /b 0

:print_report
REM Print each report entry (entries are separated by '#').
set "LINE=!REPORT!"
:print_loop
if "!LINE!"=="" goto :eof
for /f "tokens=1* delims=#" %%A in ("!LINE!") do (
    echo %%A
    set "LINE=%%B"
)
goto :print_loop
