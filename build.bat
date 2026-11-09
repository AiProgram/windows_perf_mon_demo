@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 (
    echo.
    echo Build failed.
    exit /b 1
)
echo.
echo Done. Executable: %~dp0bin\WindowsPerfMonDemo.exe
