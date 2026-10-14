@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1" -Civil3DVersion 2025 -Configuration Release
exit /b %ERRORLEVEL%
