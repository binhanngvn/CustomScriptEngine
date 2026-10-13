# Two-file shared-project example

Copy `ExamplePlugin.cs` to `BepInEx/scripts/ExamplePlugin.cs` and copy `shared/ExampleMessage.cs` to `BepInEx/scripts/shared/ExampleMessage.cs`. Restart/reload the engine, then press F10 in game to see the log message.

The folder name `shared` is organizational only; the C# namespace comes from the namespace declaration. Both files are compiled into the same runtime assembly by CustomScriptEngine V3, so `ExamplePlugin` can call `ExampleMessage.Format(...)` without a separate DLL.

The example directory under the repository is excluded from runtime script compilation, to avoid compiling the sample until you copy its files to the live `BepInEx/scripts/` folder.
