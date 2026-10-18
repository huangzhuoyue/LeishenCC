@echo off
setlocal
cd /d "%~dp0"
set "EXE=bin\Release\net5.0-windows\LeishenFanMcp.exe"

if not exist "%EXE%" (
    echo [build] %EXE% not found, building...
    dotnet build -c Release || goto :fail
)

echo ============================================================
echo  Fan hold: 240 minutes, slot follows LeishenCC's current slot
echo  Close this window or press Ctrl+C to hand control back to BIOS
echo ============================================================
"%EXE%" --hold 240
echo.
echo Hold exited, BIOS control restored.
pause
goto :eof

:fail
echo Build failed.
pause
