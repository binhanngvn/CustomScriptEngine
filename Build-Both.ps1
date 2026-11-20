[CmdletBinding()]
param(
    [switch]$MonoOnly,
    [string]$Il2CppReferenceGameDir,
    [switch]$RefreshIl2CppReference
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw '.NET SDK was not found. Install the .NET SDK, then run Build-Both.bat again.'
}

$monoProject = Join-Path $root 'src/ScriptEngine.BepInEx6.UnityMono/ScriptEngine.BepInEx6.UnityMono.csproj'
$il2cppProject = Join-Path $root 'src/ScriptEngine.BepInEx6.UnityIL2CPP/ScriptEngine.BepInEx6.UnityIL2CPP.csproj'
$monoDll = Join-Path $root 'dist/Mono/CustomScriptEngine.dll'
$il2cppDll = Join-Path $root 'dist/IL2CPP/CustomScriptEngine.IL2CPP.dll'
$referenceStore = Join-Path $root 'references/IL2CPP'
$il2cppBuilt = $false
$requiredInteropFiles = @(
    'Il2Cppmscorlib.dll',
    'UnityEngine.CoreModule.dll'
)

function Test-InteropFolder {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $false
    }

    foreach ($name in $requiredInteropFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $name) -PathType Leaf)) {
            return $false
        }
    }

    return $true
}

function Invoke-DotNetBuild {
    param(
        [string]$Project,
        [string[]]$AdditionalArguments = @()
    )

    Write-Host ''
    Write-Host "Building $Project" -ForegroundColor Cyan
    & $dotnet.Source build $Project --configuration Release --nologo @AdditionalArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE. Review the compiler diagnostics above."
    }
}

function Save-InteropSnapshot {
    param([string]$SourceInteropDir)

    if (-not (Test-InteropFolder -Path $SourceInteropDir)) {
        throw "The selected folder is not a complete IL2CPP interop folder: $SourceInteropDir"
    }

    $parent = Split-Path -Parent $referenceStore
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $tempStore = Join-Path $parent 'IL2CPP.__new'
    if (Test-Path -LiteralPath $tempStore) {
        Remove-Item -LiteralPath $tempStore -Recurse -Force
    }
    New-Item -ItemType Directory -Path $tempStore -Force | Out-Null

    $dllFiles = @(Get-ChildItem -LiteralPath $SourceInteropDir -Filter '*.dll' -File)
    if ($dllFiles.Count -eq 0) {
        throw "No interop DLL files were found in: $SourceInteropDir"
    }

    foreach ($file in $dllFiles) {
        Copy-Item -LiteralPath $file.FullName -Destination $tempStore -Force
    }

    foreach ($name in $requiredInteropFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $tempStore $name) -PathType Leaf)) {
            Remove-Item -LiteralPath $tempStore -Recurse -Force -ErrorAction SilentlyContinue
            throw "The interop snapshot is missing required file '$name'."
        }
    }

    Set-Content -LiteralPath (Join-Path $tempStore 'REFERENCE-INFO.txt') -Encoding UTF8 -Value @(
        'Build-time IL2CPP reference snapshot for CustomScriptEngine.'
        'These DLLs are used only to compile the plugin. They are not copied into dist/ and must not be installed into a game.'
        "Source interop folder: $SourceInteropDir"
        "Captured: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    )

    if (Test-Path -LiteralPath $referenceStore) {
        Remove-Item -LiteralPath $referenceStore -Recurse -Force
    }
    Move-Item -LiteralPath $tempStore -Destination $referenceStore
}

# Mono has no dependency on a selected game folder, so it can always be built alone.
if ($MonoOnly) {
    Invoke-DotNetBuild -Project $monoProject
    if (-not (Test-Path -LiteralPath $monoDll -PathType Leaf)) {
        throw "Mono build completed but the expected plugin DLL is missing: $monoDll"
    }

    Write-Host ''
    Write-Host 'Mono build succeeded.' -ForegroundColor Green
    Write-Host "Deployment folder: $(Join-Path $root 'dist/Mono')"
    return
}

# Keep using the saved reference snapshot. A different game selected for installation
# must not silently replace the build references on every run.
$hasSnapshot = Test-InteropFolder -Path $referenceStore
$explicitGameDir = $Il2CppReferenceGameDir
if ([string]::IsNullOrWhiteSpace($explicitGameDir)) {
    $explicitGameDir = $env:CSE_IL2CPP_REFERENCE_GAME_DIR
}

if (-not $hasSnapshot -and [string]::IsNullOrWhiteSpace($explicitGameDir) -and
    -not [string]::IsNullOrWhiteSpace($env:CSE_IL2CPP_GAME_DIR)) {
    # Backward compatibility: the old variable is used only to seed the reference
    # snapshot once. After that, changing it will not change future builds.
    $explicitGameDir = $env:CSE_IL2CPP_GAME_DIR
    Write-Host 'Using legacy CSE_IL2CPP_GAME_DIR once to create the IL2CPP reference snapshot.' -ForegroundColor Yellow
}

if (-not $hasSnapshot -or $RefreshIl2CppReference) {
    if ([string]::IsNullOrWhiteSpace($explicitGameDir)) {
        $explicitGameDir = Read-Host 'Optional: enter ONE IL2CPP reference game root (with BepInEx/interop generated); press Enter to build Mono only'
    }

    if (-not [string]::IsNullOrWhiteSpace($explicitGameDir)) {
        $explicitGameDir = [System.IO.Path]::GetFullPath($explicitGameDir.Trim().Trim('"'))
        $sourceInteropDir = Join-Path $explicitGameDir 'BepInEx/interop'

        if (Test-InteropFolder -Path $sourceInteropDir) {
            Save-InteropSnapshot -SourceInteropDir $sourceInteropDir
            $hasSnapshot = $true
            Write-Host "Saved reusable IL2CPP reference snapshot: $referenceStore" -ForegroundColor Green
        }
        else {
            Write-Warning "The selected game does not contain the required generated interop files in '$sourceInteropDir'."
            Write-Warning 'The IL2CPP build will be skipped. The Mono build can still succeed.'
        }
    }
}

# Always build Mono first. An IL2CPP reference problem must not block the Mono output.
Invoke-DotNetBuild -Project $monoProject
if (-not (Test-Path -LiteralPath $monoDll -PathType Leaf)) {
    throw "Mono build completed but the expected plugin DLL is missing: $monoDll"
}
Write-Host 'Mono build succeeded.' -ForegroundColor Green

if ($hasSnapshot -and (Test-InteropFolder -Path $referenceStore)) {
    Invoke-DotNetBuild -Project $il2cppProject -AdditionalArguments @("-p:InteropDir=$referenceStore")
    if (-not (Test-Path -LiteralPath $il2cppDll -PathType Leaf)) {
        throw "IL2CPP build completed but the expected plugin DLL is missing: $il2cppDll"
    }
    Write-Host 'IL2CPP build succeeded.' -ForegroundColor Green
    $il2cppBuilt = $true
}
else {
    Write-Warning 'IL2CPP build skipped: no reusable reference snapshot is configured.'
    Write-Host 'To create one, run Build-Both.bat and provide an IL2CPP game root once.'
    Write-Host 'Or run: .\Build-Both.ps1 -Il2CppReferenceGameDir "C:\Path\To\ReferenceGame"'
    Write-Host 'After the snapshot is saved, rebuilds no longer depend on the game currently being installed.'
}

Write-Host ''
Write-Host 'Build process finished.' -ForegroundColor Green
Write-Host "Mono deployment:   $(Join-Path $root 'dist/Mono')"
if ($il2cppBuilt) {
    Write-Host "IL2CPP deployment: $(Join-Path $root 'dist/IL2CPP')"
}
else {
    Write-Host 'IL2CPP was not rebuilt in this run; any previous dist/IL2CPP files were left untouched.' -ForegroundColor Yellow
}
Write-Host 'Install only the backend folder matching the target game. Do not install both into one game.'
