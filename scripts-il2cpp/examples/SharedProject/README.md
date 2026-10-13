# IL2CPP shared-script example

The IL2CPP engine compiles all eligible `.cs` files in `BepInEx/scripts-il2cpp/` (including `shared/`) into one runtime assembly. The example is excluded from automatic compilation while it remains under `examples/`.

To try it, copy:

- `ExampleScript.cs` to `BepInEx/scripts-il2cpp/ExampleScript.cs`
- `shared/ExampleMessage.cs` to `BepInEx/scripts-il2cpp/shared/ExampleMessage.cs`

Restart the game or press F6. Press F10 to show/hide the sample GUI. The script uses the `ICustomScriptEntry`, `ICustomScriptUpdate`, and `ICustomScriptOnGUI` contracts from `CustomScriptEngine.Core`; it is not a separate BepInEx plugin and does not need its own DLL.

Scripts compile as a group. A compile failure keeps the last successfully compiled project active. Under CoreCLR the current iteration loads uniquely named assemblies into the default load context; stopping callbacks does not unload their assemblies from memory, so repeated hot reloads can increase memory usage.
