@echo off
setlocal
cd /d "%~dp0"
if not exist "dist\BrokenPhone-0.19.3\BrokenPhone.exe" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0package.ps1" -ReleaseName BrokenPhone-0.19.3
if errorlevel 1 exit /b 1
start "" "%~dp0dist\BrokenPhone-0.19.3\BrokenPhone.exe"
