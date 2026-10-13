# CustomScriptEngine V3 changes

- All eligible `.cs` files under `BepInEx/scripts/` now compile together into one shared runtime assembly.
- Added a first-class `shared/` folder for reusable helper classes and common code.
- A file in one source can directly use types declared in another source (subject to normal C# namespace and visibility rules).
- File changes and F6 trigger a whole-project rebuild/reload.
- References from included script manifests and inline reference directives are merged for the shared build.
- Syntax/compile errors identify their original source file and line. Failed builds preserve the previous working project.
- Removing one source rebuilds the project from remaining sources; deleting all sources unloads the project.
- `libs/`, `.cache/`, `examples/`, `bin/`, `obj/`, and `.git/` are excluded from source compilation. `libs/*.dll` remains available as reference assemblies.
- Runtime/plugin version bumped to 3.0.0 because compilation/reload semantics changed from one DLL per file to one shared assembly.

## Compatibility note

This is a shared-project model, not one independently reloadable assembly per `.cs` file. It trades per-file reload for cross-file type access and simpler reuse. Any compile error blocks the full project update; the last working project remains active.
