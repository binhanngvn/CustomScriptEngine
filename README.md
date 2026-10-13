# CustomScriptEngine V4 — Mono + IL2CPP, one solution build

CustomScriptEngine compiles multiple C# source files together so one script can call helpers declared in other files. This package includes two backend-specific plugins built from a single Visual Studio solution:

- **Mono:** `CustomScriptEngine.dll`, targeting BepInEx 6 Unity Mono (`netstandard2.1`). Keeps the existing `BaseUnityPlugin`-based script workflow.
- **IL2CPP:** `CustomScriptEngine.IL2CPP.dll`, targeting BepInEx 6 Unity IL2CPP (`net6.0`). Uses `ICustomScriptEntry` callbacks to run ordinary managed C# script classes without requiring each script to be a separate BepInEx plugin.

The two plugin DLLs are deliberately put in separate deployment folders. Install only the DLL matching the backend of the target game; do not install both into the same game.

## Build both at once

Requirements: Windows, .NET SDK, Visual Studio 2022 or newer, and access to the configured NuGet feeds. Package restore requires network access unless the packages are already cached.

Run `Build-Both.bat` or `Build-Both.ps1`. The script asks for the root folder of an IL2CPP game with compatible BepInEx installed and its `BepInEx/interop` files already generated. Set `CSE_IL2CPP_GAME_DIR` to skip the prompt.

The solution is built once with both plugin projects. For Visual Studio, set the MSBuild property `GameDir` to that game root (for example, in the project build properties) before building **Release**. IL2CPP references are taken from the target game's generated proxy assemblies so `MonoBehaviour`/`Input` types match the IL2CPP runtime.

Expected output:

```text
dist/
├── Mono/
│   ├── CustomScriptEngine.dll
│   ├── ScriptEngine.Core.dll
│   ├── Microsoft.CodeAnalysis.dll             (plus copied runtime dependencies)
│   └── INSTALL.txt
└── IL2CPP/
    ├── CustomScriptEngine.IL2CPP.dll
    ├── ScriptEngine.Core.dll
    ├── Microsoft.CodeAnalysis.dll             (plus copied runtime dependencies)
    └── INSTALL.txt
```

The exact set of Roslyn dependency DLLs depends on package restore/runtime assets. The deployment targets exclude platform assemblies such as BepInEx, UnityEngine, Mono, Il2CppInterop, Il2Cppmscorlib and Il2CppSystem; the game supplies those at runtime.

## Install the Mono build

Copy the **contents** of `dist/Mono/` to:

```text
<Game>/BepInEx/plugins/CustomScriptEngine/
```

Source scripts stay under `BepInEx/scripts/`. The existing Mono engine compiles eligible `.cs` files in that directory and its subfolders (including `shared/`) together into one project assembly.

## Install the IL2CPP build

Copy the **contents** of `dist/IL2CPP/` to:

```text
<Game>/BepInEx/plugins/CustomScriptEngine/
```

The IL2CPP game must use a compatible BepInEx 6 IL2CPP installation. Scripts stay under the separate path:

```text
<Game>/BepInEx/scripts-il2cpp/
├── ExampleScript.cs
├── shared/
├── libs/
├── examples/
├── .cache/
└── reference-cache.txt
```

This intentionally uses `scripts-il2cpp`, rather than a subfolder of `scripts`, so the Mono loader cannot accidentally compile IL2CPP-only scripts and vice versa.

### IL2CPP script contract

IL2CPP runtime scripts are ordinary managed classes implementing `CustomScriptEngine.Core.ICustomScriptEntry`:

```csharp
using CustomScriptEngine.Core;
using UnityEngine;

public sealed class ExampleScript : ICustomScriptEntry, ICustomScriptUpdate
{
    private ICustomScriptContext? _context;

    public void OnLoad(ICustomScriptContext context)
    {
        _context = context;
        context.LogInfo("Script started");
    }

    public void OnUpdate()
    {
        if (Input.GetKeyDown(KeyCode.F10))
            _context?.LogInfo("F10 was pressed");
    }

    public void OnUnload() => _context?.LogInfo("Script stopped");
}
```

Supported optional callback interfaces are `ICustomScriptUpdate`, `ICustomScriptLateUpdate`, `ICustomScriptFixedUpdate`, and `ICustomScriptOnGUI`. The engine invokes them from an injected Unity main-thread host. Use Unity/game APIs only from the lifecycle callbacks unless a specific API is documented as thread-safe.

All eligible source files compile together, so a class in `shared/` can be called from `ExampleScript.cs`. The example under `scripts-il2cpp/examples/SharedProject/` is excluded from compilation until copied out of `examples/`.

## Important limitations

- Build output separation does not make one plugin DLL compatible with both backends. Mono and IL2CPP have different plugin entry APIs; install the correct build.
- The IL2CPP plugin project compiles against `BepInEx.Unity.IL2CPP` `6.0.0-be.788` and uses the target game's generated `BepInEx/interop/Il2Cppmscorlib.dll`, `UnityEngine.CoreModule.dll`, and `UnityEngine.InputLegacyModule.dll`. `Il2Cppmscorlib.dll` is required for the IL2CPP `Object` type used by the injected `MonoBehaviour` host. Run a compatible BepInEx IL2CPP game at least once to generate these proxies before building. This is safer than using ordinary Unity Mono module assemblies for the injected IL2CPP `MonoBehaviour` host.
- IL2CPP's source script project needs reference assemblies available in `BepInEx/interop`, BepInEx `core`/`plugins`, and `scripts-il2cpp/libs`. Missing or incompatible references will produce compiler diagnostics.
- The IL2CPP runtime script path requires a public parameterless class implementing `ICustomScriptEntry`. A helper-only file compiles but doesn't execute by itself.
- Runtime assembly loading is not the same as unloading. Reload stops script callbacks, but assemblies loaded from bytes into the default load context remain resident; repeated reload can increase memory usage. Restart the game after many reloads if necessary.
- Compilation success does not guarantee runtime/gameplay correctness. Test on a backup/offline environment first.
- This environment does not include the .NET SDK, so the package can be structurally checked here but the actual build must be verified on the Windows development machine.

## One build, two destinations

- `dist/Mono/` contains the Mono plugin and its managed runtime dependencies.
- `dist/IL2CPP/` contains the IL2CPP plugin and its managed runtime dependencies.

The root `Build-Both.bat` / `Build-Both.ps1` invokes the solution build once and verifies that both output DLLs exist. It needs the root directory of an installed IL2CPP game to reference its generated proxy assemblies; this is a build-time reference only and those game-specific DLLs are not copied into either deployment folder.
