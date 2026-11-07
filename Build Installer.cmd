@echo off
rem Double-click to build the WinTiles installer on Windows 11.
rem Installs what's needed (the .NET 10 SDK and Inno Setup) with winget the first time,
rem then builds, tests and puts the installer in artifacts\release.
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul || (
  echo Installing the .NET 10 SDK...
  winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements || goto :failed
)
if not exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" if not exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" (
  echo Installing Inno Setup...
  winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements || goto :failed
)

rem winget may have just installed dotnet; make sure this window can find it.
set "PATH=%PATH%;%ProgramFiles%\dotnet"

set ARCH=x64
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set ARCH=arm64

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Arch %ARCH% || goto :failed

echo.
echo Done. The installer is in:
echo   %~dp0artifacts\release
explorer "%~dp0artifacts\release"
pause
exit /b 0

:failed
echo.
echo The build didn't finish. The messages above say what went wrong.
pause
exit /b 1
