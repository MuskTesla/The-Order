@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0.artifacts\first-release-20261010\launch-1.0.0.ps1"
set "theOrderTestExit=%errorlevel%"
if not "%theOrderTestExit%"=="0" pause
exit /b %theOrderTestExit%
