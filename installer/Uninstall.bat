@echo off
setlocal
set "DEST=%APPDATA%\Autodesk\Revit\Addins\2026"

tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe" >nul
if not errorlevel 1 (
    echo Revit is running. Close Revit and run this again.
    pause
    exit /b 1
)

if exist "%DEST%\PipingSuite.addin" del /F /Q "%DEST%\PipingSuite.addin"
if exist "%DEST%\PipingSuite" rmdir /S /Q "%DEST%\PipingSuite"
echo Piping Productivity Suite removed.
pause
