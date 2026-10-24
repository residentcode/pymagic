@echo off
rem Double-click to build and open PyMagic. Pass a folder to open it directly: PyMagic.cmd C:\my\project
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" %*
if errorlevel 1 pause
