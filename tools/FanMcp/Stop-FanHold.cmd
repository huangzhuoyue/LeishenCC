@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

rem Stop only the --hold instance; leave MCP server processes alone.
set FOUND=0
for /f "tokens=2 delims==" %%i in ('wmic process where "name='LeishenFanMcp.exe' and commandline like '%%--hold%%'" get processid /value 2^>nul ^| find "="') do (
    if not "%%i"=="" (
        echo Killing hold process PID %%i
        taskkill /PID %%i /F >nul 2>&1
        set FOUND=1
    )
)

if "!FOUND!"=="0" (
    echo No --hold process found.
) else (
    echo Hold stopped. Fan control handed back to BIOS.
)
pause
