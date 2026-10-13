@echo off
setlocal
cd /d "%~dp0"

echo ================================================
echo CustomScriptEngine - Build Mono + IL2CPP Release
echo ================================================

dotnet --version >nul 2>&1
if errorlevel 1 (
  echo ERROR: .NET SDK is not installed or dotnet is not on PATH.
  echo Install the .NET SDK required by the solution, then run this file again.
  pause
  exit /b 1
)

set "IL2CPP_GAME_DIR=%CSE_IL2CPP_GAME_DIR%"
if "%IL2CPP_GAME_DIR%"=="" (
  echo.
  echo IL2CPP build needs one installed IL2CPP game with BepInEx installed and interop generated.
  echo The game's BepInEx\interop folder must contain Il2Cppmscorlib.dll and UnityEngine.CoreModule.dll.
  set /p "IL2CPP_GAME_DIR=Enter full path to the IL2CPP game root: "
)

if not exist "%IL2CPP_GAME_DIR%\BepInEx\interop\Il2Cppmscorlib.dll" (
  echo ERROR: Could not find "%IL2CPP_GAME_DIR%\BepInEx\interop\Il2Cppmscorlib.dll"
  echo The IL2CPP interop proxies are incomplete. Run the game with compatible BepInEx IL2CPP once first.
  pause
  exit /b 1
)
if not exist "%IL2CPP_GAME_DIR%\BepInEx\interop\UnityEngine.CoreModule.dll" (
  echo ERROR: Could not find "%IL2CPP_GAME_DIR%\BepInEx\interop\UnityEngine.CoreModule.dll"
  echo Select the game root, not its BepInEx folder. Run the game with BepInEx IL2CPP once first.
  pause
  exit /b 1
)
if not exist "%IL2CPP_GAME_DIR%\BepInEx\interop\UnityEngine.InputLegacyModule.dll" (
  echo ERROR: Could not find "%IL2CPP_GAME_DIR%\BepInEx\interop\UnityEngine.InputLegacyModule.dll"
  echo The current script engine uses UnityEngine.Input for its F6 reload hotkey.
  echo Use an IL2CPP game whose BepInEx setup generated this proxy assembly.
  pause
  exit /b 1
)

echo.
echo Building the solution once using IL2CPP references from:
echo   %IL2CPP_GAME_DIR%\BepInEx\interop
echo.
dotnet build "CustomScriptEngine.sln" --configuration Release --nologo "-p:GameDir=%IL2CPP_GAME_DIR%"
if errorlevel 1 (
  echo.
  echo BUILD FAILED. Check the errors above. No successful build is claimed.
  pause
  exit /b 1
)

echo.
echo Build succeeded. Deployment folders:
echo   %~dp0dist\Mono\
echo   %~dp0dist\IL2CPP\
if not exist "%~dp0dist\Mono\CustomScriptEngine.dll" (
  echo WARNING: Mono deployment DLL was not found at expected path.
)
if not exist "%~dp0dist\IL2CPP\CustomScriptEngine.IL2CPP.dll" (
  echo WARNING: IL2CPP deployment DLL was not found at expected path.
)
echo.
echo Copy only the matching folder's contents into the game's BepInEx\plugins\CustomScriptEngine folder.
echo Never copy both backend DLLs into the same game; choose the matching Mono or IL2CPP folder.
pause
