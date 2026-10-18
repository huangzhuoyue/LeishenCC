@echo off
setlocal
cd /d "%~dp0"

if not exist "out\LeishenFanMcp.exe" (
    echo [build] out\LeishenFanMcp.exe not found, building...
    dotnet build -c Release -o out || goto :fail
)

echo ============================================================
echo  Fan hold: slot 1, 240 minutes
echo  Close this window or press Ctrl+C to hand control back to BIOS
echo ============================================================
"out\LeishenFanMcp.exe" --hold 240 1
echo.
echo Hold exited, BIOS control restored.
pause
goto :eof

:fail
echo Build failed.
pause
