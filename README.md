# CustomScriptEngine V4 — Mono + IL2CPP backends

CustomScriptEngine compiles multiple C# source files together so one script can call helpers declared in other files. This package includes two backend-specific plugins in one Visual Studio solution:

- **Mono:** `CustomScriptEngine.dll`, targeting BepInEx 6 Unity Mono (`netstandard2.1`). Keeps the existing `BaseUnityPlugin`-based script workflow.
- **IL2CPP:** `CustomScriptEngine.IL2CPP.dll`, targeting BepInEx 6 Unity IL2CPP (`net6.0`). Uses `ICustomScriptEntry` callbacks to run ordinary managed C# script classes without requiring each script to be a separate BepInEx plugin.

The two plugin DLLs are deliberately put in separate deployment folders. Install only the DLL matching the backend of the target game; do not install both into the same game.

## Build both backends

Requirements: Windows, .NET SDK, and access to the configured NuGet feeds. Package restore requires network access unless the packages are already cached.

Run `Build-Both.bat`. The Mono plugin is built without requiring any game folder. The IL2CPP plugin uses a **separate build-time reference snapshot** stored in `references/IL2CPP/`:

- On the first build, the script optionally asks for one IL2CPP reference game's root. That game must already have compatible BepInEx 6 installed and have been launched at least once so `BepInEx/interop` is generated.
- The script copies the generated interop DLLs into `references/IL2CPP/` and builds IL2CPP against that snapshot.
- Later builds reuse the snapshot. Changing the game where you intend to install the plugin does not change the build references and does not require rebuilding for each game.
- Press Enter at the first prompt to build Mono only. You can create the IL2CPP snapshot later.

To build only Mono, run `Build-MonoOnly.bat` (or `Build-Both.ps1 -MonoOnly`). To deliberately replace the saved IL2CPP reference snapshot with one from another reference game, run:

```powershell
.\Build-Both.ps1 -Il2CppReferenceGameDir "C:\Path\To\ReferenceGame" -RefreshIl2CppReference
```

The reference snapshot is for compilation only. It is not copied into `dist/` and must not be installed into a game. At runtime, CustomScriptEngine discovers the assemblies from the actual target game so that user scripts compile against that game's current API.

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
- The IL2CPP plugin project compiles against `BepInEx.Unity.IL2CPP` `6.0.0-be.788` and a saved build-time interop snapshot containing `Il2Cppmscorlib.dll` and `UnityEngine.CoreModule.dll`. These are proxy assemblies generated by BepInEx for an IL2CPP game; ordinary Unity Mono module assemblies are not substitutes for the injected IL2CPP `MonoBehaviour` host. The optional F6 hotkey resolves `UnityEngine.Input` by reflection, so the legacy Input module is no longer a mandatory build reference; if it is unavailable at runtime, automatic file watching still works.
- The saved snapshot removes the need to point every build at the game currently receiving the plugin. It does **not** guarantee compatibility with every Unity version or every generated interop set. If a different target game fails to load the IL2CPP plugin with missing type/method errors, refresh the snapshot from that game's `BepInEx/interop` folder and rebuild, then test again.
- IL2CPP's source script project needs reference assemblies available in `BepInEx/interop`, BepInEx `core`/`plugins`, and `scripts-il2cpp/libs`. Missing or incompatible references will produce compiler diagnostics.
- The IL2CPP runtime script path requires a public parameterless class implementing `ICustomScriptEntry`. A helper-only file compiles but doesn't execute by itself.
- Runtime assembly loading is not the same as unloading. Reload stops script callbacks, but assemblies loaded from bytes into the default load context remain resident; repeated reload can increase memory usage. Restart the game after many reloads if necessary.
- Compilation success does not guarantee runtime/gameplay correctness. Test on a backup/offline environment first.
- This environment does not include the .NET SDK, so the package can be structurally checked here but the actual build must be verified on the Windows development machine.

## Build outputs and installing on other games

- `dist/Mono/` contains the Mono plugin and its managed runtime dependencies.
- `dist/IL2CPP/` contains the IL2CPP plugin and its managed runtime dependencies.

The two outputs are different plugins because BepInEx Mono and BepInEx IL2CPP have different entry-point and Unity integration APIs. For each target game, install only the matching backend folder. You do not need to rebuild just because you are switching to another game: copy the already-built matching folder into that game's `BepInEx/plugins/CustomScriptEngine/` directory. Rebuild only when changing the engine source or intentionally refreshing the IL2CPP reference snapshot.

This project targets Unity games running a compatible BepInEx 6 backend. It cannot load into arbitrary non-Unity games, games without BepInEx, or use one identical DLL for both Mono and IL2CPP.
