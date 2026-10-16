@echo off
setlocal
rem Piping Productivity Suite - per-user installer for Revit 2026 (no admin rights needed).
set "SRC=%~dp0"
set "DEST=%APPDATA%\Autodesk\Revit\Addins\2026"

tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe" >nul
if not errorlevel 1 (
    echo Revit is running. Close Revit and run this installer again.
    pause
    exit /b 1
)

echo Installing Piping Productivity Suite to:
echo   %DEST%
if not exist "%DEST%" mkdir "%DEST%"

rem Replace any previous version so stale DLLs are not left behind.
if exist "%DEST%\PipingSuite" rmdir /S /Q "%DEST%\PipingSuite"
xcopy "%SRC%PipingSuite" "%DEST%\PipingSuite\" /E /I /Y /Q >nul || goto :fail
copy /Y "%SRC%PipingSuite.addin" "%DEST%\" >nul || goto :fail

rem Clear the "downloaded from internet" flag so Windows does not block the DLLs.
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%DEST%\PipingSuite' -Recurse | Unblock-File" >nul 2>&1

echo.
echo Done. Start Revit 2026 and open the "Piping Suite" tab.
echo On first launch, choose "Always Load" when Revit asks about the add-in.
pause
exit /b 0

:fail
echo.
echo Installation failed. Check that you have write access to %DEST%.
pause
exit /b 1
