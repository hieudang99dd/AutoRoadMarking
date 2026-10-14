@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-all.ps1" -Configuration Release
exit /b %ERRORLEVEL%
