# CustomScriptEngine patch notes

## Changes

- Build scripts no longer bind every IL2CPP build to the game currently being targeted. The first IL2CPP build can snapshot one reference game's generated `BepInEx/interop` DLLs into `references/IL2CPP/`; subsequent builds reuse that snapshot.
- Mono can be built independently with `Build-MonoOnly.bat` or `Build-Both.ps1 -MonoOnly`.
- An incomplete or missing IL2CPP reference set skips the IL2CPP build without blocking the Mono build.
- The IL2CPP plugin no longer has a compile-time reference to `UnityEngine.InputLegacyModule`; its optional reload hotkey resolves the old Input API with reflection. File watching remains available if that module is unavailable.
- Removed an orphaned duplicate metadata-reference block from `RoslynCompiler.cs` that referenced a loop variable outside its scope.

## Important compatibility note

Mono and IL2CPP still require separate plugin DLLs. Install `dist/Mono/` only in a compatible BepInEx 6 Mono game, and `dist/IL2CPP/` only in a compatible BepInEx 6 IL2CPP game. A saved IL2CPP reference snapshot makes the build reusable across many games, but cannot guarantee compatibility with every Unity version or every generated interop API.

## Build validation

The source and project XML were statically checked. This environment does not have the .NET SDK or PowerShell, so the Windows build and runtime loading tests have not been executed. The ZIP intentionally omits the old `dist/` binaries and intermediate `artifacts/`/`obj/` outputs so they cannot be mistaken for newly rebuilt binaries. Run `Build-Both.bat` on the Windows development machine to generate fresh output.
