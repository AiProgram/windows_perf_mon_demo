@echo off
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
  /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /out:"%~dp0bin\UiHang.exe" "%~dp0UiHang.cs"
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)
echo Built: %~dp0bin\UiHang.exe
