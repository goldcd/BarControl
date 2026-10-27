@echo off
echo Uninstalling BarControl...
powershell -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
echo Done.
timeout /t 2 >nul
