@echo off
rem PyMagic for Windows. Double-click to open the app. A folder can be given: PyMagic.cmd C:\my\project
cd /d "%~dp0desktop"
if not exist node_modules\electron\dist\electron.exe (
  echo Installing the app shell, first run only...
  call npm install
  node node_modules\electron\install.js
  if errorlevel 1 ( echo Installing failed. & pause & exit /b 1 )
)
start "" node_modules\electron\dist\electron.exe . %*
