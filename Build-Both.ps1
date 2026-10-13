$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw '.NET SDK was not found. Install the .NET SDK, then run Build-Both.ps1 again.'
}

$gameDir = $env:CSE_IL2CPP_GAME_DIR
if ([string]::IsNullOrWhiteSpace($gameDir)) {
    $gameDir = Read-Host 'Enter full path to an IL2CPP game root with BepInEx installed and interop generated'
}
$gameDir = [System.IO.Path]::GetFullPath($gameDir.Trim().Trim('"'))
$interopDir = Join-Path $gameDir 'BepInEx/interop'
$il2cppMscorlib = Join-Path $interopDir 'Il2Cppmscorlib.dll'
$coreModule = Join-Path $interopDir 'UnityEngine.CoreModule.dll'
$inputModule = Join-Path $interopDir 'UnityEngine.InputLegacyModule.dll'
if (-not (Test-Path $il2cppMscorlib)) {
    throw "Missing $il2cppMscorlib. The IL2CPP interop proxies are incomplete. Run the game with compatible BepInEx IL2CPP once, then rebuild."
}
if (-not (Test-Path $coreModule)) {
    throw "Missing $coreModule. Choose the game root and run the game with compatible BepInEx IL2CPP once to generate interop files."
}
if (-not (Test-Path $inputModule)) {
    throw "Missing $inputModule. The current script engine uses UnityEngine.Input for its F6 reload hotkey; select a game whose interop setup generated this proxy assembly."
}

Write-Host "Using IL2CPP reference assemblies: $interopDir"
& dotnet build (Join-Path $root 'CustomScriptEngine.sln') --configuration Release --nologo "-p:GameDir=$gameDir"
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE. Check the compiler diagnostics above."
}

$mono = Join-Path $root 'dist/Mono/CustomScriptEngine.dll'
$il2cpp = Join-Path $root 'dist/IL2CPP/CustomScriptEngine.IL2CPP.dll'
if (-not (Test-Path $mono)) { throw "Expected Mono plugin was not produced: $mono" }
if (-not (Test-Path $il2cpp)) { throw "Expected IL2CPP plugin was not produced: $il2cpp" }

Write-Host ''
Write-Host 'Build succeeded. Both backend-specific deployment folders are ready:' -ForegroundColor Green
Write-Host "  Mono:   $(Join-Path $root 'dist/Mono')"
Write-Host "  IL2CPP: $(Join-Path $root 'dist/IL2CPP')"
Write-Host 'Copy only the matching folder contents to a compatible game. Do not install both backend DLLs into the same game.'
