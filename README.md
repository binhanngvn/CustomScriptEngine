# CustomScriptEngine V2 — BepInEx 6 Unity Mono

## What is CustomScriptEngine?

CustomScriptEngine is a runtime C# script engine for compatible Unity Mono games running under BepInEx 6. It allows `.cs` scripts to be compiled with Roslyn and loaded by the engine without manually building a separate plugin for every script change.

The engine is intended for offline Unity games that the user owns or is authorized to modify. Compatibility depends on the game's Unity/Mono runtime and the assemblies available to the engine.

## Original project and V2

V2 continues the original CustomScriptEngine hot-reload workflow and updates how game and plugin references are discovered.

Main capabilities:

- Compile `.cs` scripts at runtime using Roslyn.
- Reload scripts with F6.
- Watch script files for changes.
- Keep the previous working script version if a new compilation fails.
- Discover assemblies in the game's `*_Data/Managed`, `BepInEx/core`, `BepInEx/plugins`, and `BepInEx/scripts/libs` directories.
- Cache discovered assembly paths in `BepInEx/scripts/reference-cache.txt`.
- Support script manifests, `// @reference ...` directives, and manually added DLL references.
- Recompile scripts when relevant DLL references change.

## Requirements and compatibility

The current target configuration is:

- BepInEx 6 — Unity Mono build
- Windows x64
- Unity 2022.1.24f1 / CLR 4.0 Mono
- .NET Standard 2.1 engine build target
- A compatible Unity Mono game with accessible managed assemblies

This is not an IL2CPP engine. IL2CPP support would require a separate compatibility and testing effort.

## Build from source

1. Install a compatible version of Visual Studio with the .NET desktop/development components needed by the solution.
2. Open `CustomScriptEngine.sln`.
3. Select the configuration you want to build (`Debug` for testing or `Release` for a release build).
4. Build the solution.
5. Find the generated output directory for the `ScriptEngine.BepInEx6.UnityMono` project.

For a Debug build, the output path used by this project is:

```text
CustomScriptEngine\src\ScriptEngine.BepInEx6.UnityMono\bin\Debug\netstandard2.1\
```

If you build in Release mode, use the corresponding `Release` output directory instead. The exact output location can vary if the project configuration has been changed.

## Install into a game

The engine's deployment folder should be:

```text
<Game>\BepInEx\plugins\CustomScriptEngine\
```

Copy the engine DLL and the runtime dependency DLLs produced in the project's build output into that folder.

If you are unsure which generated DLLs are dependencies, the desktop installer can copy the DLL files from the selected build output directory into `BepInEx\plugins\CustomScriptEngine\`. It must not copy unrelated files from other directories, and it must not bundle BepInEx or Unity platform assemblies that are supplied by the game/BepInEx runtime.

Example layout:

```text
<Game>\
└─ BepInEx\
   ├─ plugins\
   │  └─ CustomScriptEngine\
   │     ├─ ScriptEngine.BepInEx6.UnityMono.dll
   │     └─ [other required engine dependency DLLs]
   └─ scripts\
      ├─ [your script].cs
      ├─ [optional script manifest].json
      ├─ libs\
      │  └─ [optional manually supplied reference DLLs]
      └─ reference-cache.txt
```

The desktop app should create `BepInEx\plugins\CustomScriptEngine\` when needed and copy the selected, verified engine files there. It should not overwrite existing files silently; show the source, destination, and file list and ask for confirmation first.

Do not copy the entire game `Managed` directory or the whole `BepInEx\core` directory into the engine plugin folder. Those assemblies are references supplied by the game or BepInEx, not engine deployment files.

## Script folder

On first launch, the engine creates or uses:

```text
<Game>\BepInEx\scripts\
```

Place user-created `.cs` scripts there. Optional script manifests and manually supplied reference DLLs can be placed beside the scripts or in `scripts\libs\`, as supported by the engine configuration.

The engine writes its discovered assembly paths to:

```text
<Game>\BepInEx\scripts\reference-cache.txt
```

## Important notes

- A successful build does not guarantee compatibility with every Unity Mono game.
- Use the engine only with games and files you are authorized to modify.
- Runtime script compilation and hot reload can fail when a script references unavailable assemblies or incompatible game APIs.
- Mono's default AppDomain cannot truly unload an old user assembly. Reloading may retain some assembly memory over time.
- Keep a backup of the game and test changes before using them in a main save.

## Source and release integrity

Only download release files from the project's verified release source. Before installing, check the release version and compatibility. Where a trusted checksum or signature is published, verify it. If the source or file integrity cannot be verified, do not install the files.
