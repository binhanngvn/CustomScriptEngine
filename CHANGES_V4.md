# CustomScriptEngine V4

## Added
- Added a dedicated BepInEx 6 Unity IL2CPP plugin project (`CustomScriptEngine.IL2CPP.dll`).
- Added backend-specific runtime script API contracts to `ScriptEngine.Core`: `ICustomScriptEntry`, `ICustomScriptContext`, and optional Update/LateUpdate/FixedUpdate/OnGUI callbacks.
- Added an IL2CPP injected Unity host to dispatch user script callbacks on Unity's main thread.
- Added a separate script root at `BepInEx/scripts-il2cpp/` with its own `shared/`, `libs/`, `examples/`, `.cache/`, and reference cache.
- Added IL2CPP reference discovery for `BepInEx/interop`, `core`, `plugins`, game Managed directories, and script libraries.
- Added `Build-Both.bat` and `Build-Both.ps1`; one solution build prepares two distinct deployment folders.

## Changed
- Mono and IL2CPP plugin build outputs deploy to `dist/Mono/` and `dist/IL2CPP/` respectively.
- Existing Mono script behavior is preserved. IL2CPP scripts use ordinary managed entry classes rather than inheriting the Mono-only `BaseUnityPlugin` type.

## Limitations
- Actual compilation was not run in the authoring environment because the .NET SDK is unavailable there. Restore/build and in-game smoke testing must be completed on the Windows development machine.
- IL2CPP runtime script assembly reload stops callbacks but cannot physically unload assemblies already loaded into the default CoreCLR context. Repeated reloads may accumulate memory until game restart.
- Use the target game's compatible BepInEx 6 IL2CPP build and verify the UnityEngine API package version before deployment.


## Build configuration correction
- Removed the generic `UnityEngine.Modules` NuGet reference from the IL2CPP plugin project.
- The IL2CPP plugin now references the target game's generated `BepInEx/interop/UnityEngine.CoreModule.dll`; it validates `UnityEngine.InputLegacyModule.dll` for the F6 hotkey.
- `Build-Both.bat` and `Build-Both.ps1` prompt for an IL2CPP game root (or accept `CSE_IL2CPP_GAME_DIR`) and pass `GameDir` to a single solution build.
- Added a clear build-time error when the game root is missing or its IL2CPP proxy assembly has not been generated.
- Validation note: .NET SDK is unavailable in this authoring environment, so XML/package structure was checked but an actual compile and in-game test remain necessary.


## V4.1 hotfix: IL2CPP reference

- Added an explicit compile-time reference to `BepInEx/interop/Il2Cppmscorlib.dll`. This resolves CS0012 errors for `System.Object`'s IL2CPP proxy type at the injected `ScriptEngineHost : MonoBehaviour` and its pointer constructor.
- Added clear checks in both build scripts and the IL2CPP project validation target so incomplete interop output fails early with a specific message.
- Excluded `Il2Cppmscorlib.dll` and `Il2CppSystem*.dll` from deployment payloads because the target game's BepInEx interop/runtime provides them.
- The NU1603 Cpp2IL dependency warning is unchanged; it is a NuGet version-resolution warning, not the cause of CS0012.
- Build execution remains unverified in this environment because the .NET SDK is unavailable. Please rebuild on Windows and share any new compiler diagnostics.
