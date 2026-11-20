@echo off
setlocal
cd /d "%~dp0"

echo ================================================
echo CustomScriptEngine - Build Mono + IL2CPP
echo ================================================
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Both.ps1" %*
set "BUILD_EXIT=%ERRORLEVEL%"

echo.
if not "%CSE_NO_PAUSE%"=="1" pause
exit /b %BUILD_EXIT%
