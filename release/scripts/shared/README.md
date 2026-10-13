# Shared source folder

Put reusable `.cs` helper classes here. CustomScriptEngine V3 compiles them in the same runtime assembly as eligible `.cs` files under `BepInEx/scripts/`. They do not need a separate DLL.

A folder name does not define a C# namespace. Declare a namespace in the source and use the matching `using` directive from other source files, unless both types are in the same namespace.

For a complete example, see `scripts/examples/SharedProject/` in the engine source package.
