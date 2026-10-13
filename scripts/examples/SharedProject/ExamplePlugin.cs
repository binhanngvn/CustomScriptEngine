// Copy this file to BepInEx/scripts/ExamplePlugin.cs to try cross-file calls.
using BepInEx;
using BepInEx.Unity.Mono;
using UnityEngine;
using CustomScriptEngine.Examples.Shared;

[BepInPlugin("com.example.sharedscript", "Shared Script Example", "1.0.0")]
public sealed class ExamplePlugin : BaseUnityPlugin
{
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F10))
            Logger.LogInfo(ExampleMessage.Format("The shared helper was called from another .cs file."));
    }
}
