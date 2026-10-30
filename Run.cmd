@echo off
setlocal
cd /d "%~dp0"
if not exist "dist\BrokenPhone\BrokenPhone.exe" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0package.ps1"
if errorlevel 1 exit /b 1
start "" "%~dp0dist\BrokenPhone\BrokenPhone.exe"
