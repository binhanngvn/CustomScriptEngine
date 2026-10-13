// Copy this file to <Game>/BepInEx/scripts-il2cpp/ExampleScript.cs
// Copy shared/ExampleMessage.cs to <Game>/BepInEx/scripts-il2cpp/shared/ExampleMessage.cs
using CustomScriptEngine.Core;
using UnityEngine;

namespace CustomScriptEngine.Examples;

public sealed class ExampleScript : ICustomScriptEntry, ICustomScriptUpdate, ICustomScriptOnGUI
{
    private ICustomScriptContext? _context;
    private bool _showWindow;
    private float _nextStatusLog;

    public void OnLoad(ICustomScriptContext context)
    {
        _context = context;
        context.LogInfo(ExampleMessage.Build("IL2CPP"));
        context.LogInfo("Example loaded. Press F10 to show/hide the small test window.");
    }

    public void OnUpdate()
    {
        if (Input.GetKeyDown(KeyCode.F10))
            _showWindow = !_showWindow;

        if (Time.unscaledTime >= _nextStatusLog)
        {
            _nextStatusLog = Time.unscaledTime + 10f;
            // Intentional low-frequency demo log; avoid logging on every frame.
            _context?.LogInfo("Example script update callback is running.");
        }
    }

    public void OnGUI()
    {
        if (!_showWindow)
            return;

        GUILayout.BeginArea(new Rect(24, 24, 320, 120), GUI.skin.box);
        GUILayout.Label("CustomScriptEngine IL2CPP example");
        GUILayout.Label("This managed script uses a helper in shared/.");
        if (GUILayout.Button("Hide (F10)"))
            _showWindow = false;
        GUILayout.EndArea();
    }

    public void OnUnload()
    {
        _context?.LogInfo("Example script unloaded.");
        _context = null;
    }
}
