@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found. .NET Framework 4.x is required.
  exit /b 1
)
if not exist "%~dp0build" mkdir "%~dp0build"

rem --- pack both skins into one archive that gets embedded in the exe ---
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack-skins.ps1"
if not exist "%~dp0build\skins.zip" (
  echo Failed to build skins.zip
  exit /b 1
)

"%CSC%" -nologo -target:winexe -unsafe -optimize+ -out:"%~dp0build\CfosWidget.exe" ^
  -win32icon:"%~dp0icon\icon.ico" ^
  -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Core.dll ^
  -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll ^
  -resource:"%~dp0build\skins.zip",skins.zip ^
  "%~dp0src\Ini.cs" "%~dp0src\Metrics.cs" "%~dp0src\Skin.cs" "%~dp0src\SkinPack.cs" "%~dp0src\Program.cs"
if errorlevel 1 exit /b 1
if exist "%~dp0build\skins.zip" del "%~dp0build\skins.zip"
echo.
echo Build OK: %~dp0build\CfosWidget.exe
endlocal
