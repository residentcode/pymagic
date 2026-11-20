@echo off
rem Builds PyMagic on a new PC: checks the tools it needs (offers to install what is missing with winget), installs the npm packages,
rem then builds desktop\out\PyMagic-win32-x64\PyMagic.exe and the installer desktop\out\PyMagic-Setup-<version>.exe.
rem   Build.cmd                 the app and its installer
rem   Build.cmd --no-installer  the app folder only
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
echo == PyMagic build
echo.

rem ---- .NET 9 SDK
set "HAVE_DOTNET="
for /f "tokens=1 delims=." %%v in ('dotnet --list-sdks 2^>nul') do if %%v GEQ 9 set "HAVE_DOTNET=1"
if not defined HAVE_DOTNET (
  call :install ".NET 9 SDK" Microsoft.DotNet.SDK.9 "%ProgramFiles%\dotnet" || exit /b 1
  set "HAVE_DOTNET="
  for /f "tokens=1 delims=." %%v in ('dotnet --list-sdks 2^>nul') do if %%v GEQ 9 set "HAVE_DOTNET=1"
  if not defined HAVE_DOTNET ( echo The .NET 9 SDK is still not found. Open a new window and run Build.cmd again. & goto :fail )
)
echo   .NET SDK  ok

rem ---- Node.js 20 or later
set "NODE_MAJOR=0"
for /f %%v in ('node -p "process.versions.node.split('.')[0]" 2^>nul') do set "NODE_MAJOR=%%v"
if !NODE_MAJOR! LSS 20 (
  call :install "Node.js LTS" OpenJS.NodeJS.LTS "%ProgramFiles%\nodejs" || exit /b 1
  set "NODE_MAJOR=0"
  for /f %%v in ('node -p "process.versions.node.split('.')[0]" 2^>nul') do set "NODE_MAJOR=%%v"
  if !NODE_MAJOR! LSS 20 ( echo Node.js 20 or later is still not found. Open a new window and run Build.cmd again. & goto :fail )
)
echo   Node.js   ok (v!NODE_MAJOR!)

rem ---- the build is locked out of desktop\out while a PyMagic.exe from there runs
tasklist /fi "imagename eq PyMagic.exe" 2>nul | find /i "PyMagic.exe" >nul && (
  echo.
  echo PyMagic.exe is running. Close it if it was started from desktop\out, then press a key.
  pause >nul
)

echo.
echo == npm packages
pushd web
call npm install --no-audit --no-fund || ( popd & goto :fail )
popd
pushd desktop
call npm install --no-audit --no-fund || ( popd & goto :fail )
popd

echo.
echo == building (the first build downloads Electron, the .NET runtime and the 531 MB completion model)
node desktop\package.mjs %* || goto :fail

echo.
echo Done.
echo   App:       %~dp0desktop\out\PyMagic-win32-x64\PyMagic.exe
if /i not "%~1"=="--no-installer" for %%f in ("%~dp0desktop\out\PyMagic-Setup-*.exe") do echo   Installer: %%~ff
echo.
pause
exit /b 0

rem ---- :install "name" winget-id folder-to-add-to-PATH
:install
echo.
echo %~1 is not installed.
where winget >nul 2>nul || ( echo Install it yourself, then run Build.cmd again. & goto :fail )
choice /m "Install %~1 now with winget"
if errorlevel 2 ( echo Install it yourself, then run Build.cmd again. & goto :fail )
winget install --id %~2 -e --accept-package-agreements --accept-source-agreements
rem this window does not see the new PATH yet: add the usual install folder for the rest of the build
set "PATH=%~3;%PATH%"
exit /b 0

:fail
echo.
echo Build failed.
pause
exit /b 1
