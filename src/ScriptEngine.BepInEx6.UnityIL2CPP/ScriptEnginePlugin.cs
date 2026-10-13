using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using CustomScriptEngine.Core;
using System.Collections.Concurrent;
using System.Reflection;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace CustomScriptEngine.BepInEx6.UnityIL2CPP;

[BepInPlugin(GUID, NAME, VERSION)]
public sealed class ScriptEnginePlugin : BasePlugin
{
    public const string GUID = "com.custom.scriptengine.bepinex6.unityil2cpp";
    public const string NAME = "Custom Script Engine (IL2CPP)";
    public const string VERSION = "3.1.0";

    internal static ScriptEnginePlugin? Instance { get; private set; }

    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private ScriptEngineService? _service;
    private CustomScriptProjectRuntime? _runtime;
    private CustomScriptContext? _context;
    private ScriptEngineHost? _host;
    private ConfigEntry<KeyCode>? _reloadKey;
    private ConfigEntry<bool>? _watcherEnabled;
    private ConfigEntry<float>? _debounce;
    private ConfigEntry<bool>? _autoDiscoverReferences;
    private string? _projectKey;

    public override void Load()
    {
        Instance = this;

        var bepinexRoot = Paths.BepInExRootPath;
        var gameRoot = Paths.GameRootPath;
        var scripts = Path.Combine(bepinexRoot, "scripts-il2cpp");
        var cache = Path.Combine(scripts, ".cache");
        var libs = Path.Combine(scripts, "libs");
        var shared = Path.Combine(scripts, "shared");
        var referenceCache = Path.Combine(scripts, "reference-cache.txt");

        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(libs);
        Directory.CreateDirectory(shared);

        _reloadKey = Config.Bind("General", "ReloadKey", KeyCode.F6,
            "Recompile and reload the complete shared IL2CPP script project.");
        _watcherEnabled = Config.Bind("General", "EnableFileSystemWatcher", true,
            "Automatically rebuild scripts-il2cpp after C# files change.");
        _debounce = Config.Bind("General", "AutoReloadDelay", 0.5f,
            "Delay in seconds after a script change before compiling.");
        _autoDiscoverReferences = Config.Bind("General", "AutoDiscoverReferences", true,
            "Discover BepInEx, IL2CPP interop and game managed DLL references at startup.");

        _projectKey = Path.Combine(cache, "__SharedIL2CPPScriptProject__.cs");
        _runtime = new CustomScriptProjectRuntime(message => Log.LogInfo(message));
        _context = new CustomScriptContext(
            gameRoot,
            bepinexRoot,
            scripts,
            message => Log.LogInfo(message),
            message => Log.LogWarning(message),
            message => Log.LogError(message));

        _service = new ScriptEngineService(scripts, cache, _debounce.Value);
        _service.AddReferenceDirectory(Path.Combine(bepinexRoot, "core"));
        _service.AddReferenceDirectory(Path.Combine(bepinexRoot, "plugins"));
        _service.AddReferenceDirectory(Path.Combine(bepinexRoot, "interop"));
        _service.AddReferenceDirectory(libs);
        _service.AddReferenceDirectory(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty);

        _service.Log += message => Log.LogInfo(message);
        _service.ProcessingError += (path, ex) => Log.LogError($"{path}: {ex}");
        _service.CompilationFinished += result =>
        {
            if (result.Success)
                Log.LogInfo("IL2CPP shared script project compiled successfully.");
            else
            {
                Log.LogError("IL2CPP script compilation failed; the previous active project is being kept.");
                foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
                    Log.LogError($"{diagnostic.Id}: {diagnostic.Message} ({diagnostic.FilePath}:{diagnostic.Line}:{diagnostic.Column})");
            }
        };
        _service.CompilationSucceeded += (scriptPath, assemblyPath) =>
            _mainThreadActions.Enqueue(() => ActivateCompiledProject(scriptPath, assemblyPath));
        _service.ProjectCleared += scriptPath => _mainThreadActions.Enqueue(() =>
        {
            if (_runtime != null)
                _runtime.Unload(scriptPath);
            Log.LogInfo("IL2CPP shared script project unloaded because no eligible C# source files remain.");
        });

        var discoveryDirectories = FindReferenceDiscoveryDirectories(gameRoot, bepinexRoot);
        discoveryDirectories.Add(Path.Combine(bepinexRoot, "core"));
        discoveryDirectories.Add(Path.Combine(bepinexRoot, "plugins"));
        discoveryDirectories.Add(Path.Combine(bepinexRoot, "interop"));
        discoveryDirectories.Add(libs);
        discoveryDirectories.Add(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty);

        _service.ConfigureCachedReferences(
            enabled: true,
            cacheFilePath: referenceCache,
            discoveryDirectories: discoveryDirectories,
            scanNow: _autoDiscoverReferences.Value);

        if (_watcherEnabled.Value)
            _service.StartWatcher();

        // The host is a registered IL2CPP MonoBehaviour, used only to dispatch Unity
        // callbacks on the Unity main thread. User scripts remain ordinary managed types.
        _host = AddComponent<ScriptEngineHost>();

        Log.LogInfo($"{NAME} {VERSION} loaded.");
        Log.LogInfo($"Scripts folder: {scripts}");
        Log.LogInfo($"Reference cache: {referenceCache}");
        Log.LogInfo("IL2CPP scripts use ICustomScriptEntry and optional update callbacks from CustomScriptEngine.Core.");
        Log.LogInfo("Shared .cs files compile into one project. Press F6 to rebuild; examples/, libs/ and .cache/ are excluded from source compilation.");

        _mainThreadActions.Enqueue(() =>
        {
            _service?.LoadReferenceCacheFromDisk();
            _service?.ReloadAll();
        });
    }

    private static List<string> FindReferenceDiscoveryDirectories(string gameRoot, string bepinexRoot)
    {
        var result = new List<string>();
        var interop = Path.Combine(bepinexRoot, "interop");
        if (Directory.Exists(interop))
            result.Add(interop);

        try
        {
            foreach (var dataDirectory in Directory.EnumerateDirectories(gameRoot, "*_Data", SearchOption.TopDirectoryOnly))
            {
                var managed = Path.Combine(dataDirectory, "Managed");
                if (Directory.Exists(managed))
                    result.Add(managed);
            }
        }
        catch (Exception ex)
        {
            Instance?.Log.LogWarning($"Could not enumerate Unity Managed directories: {ex.Message}");
        }

        return result.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal void OnFrameUpdate()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Log.LogError($"Queued main-thread action failed: {ex}"); }
        }

        if (_reloadKey != null && Input.GetKeyDown(_reloadKey.Value))
            _service?.ReloadAll();

        _runtime?.OnUpdate();
    }

    internal void OnFrameLateUpdate() => _runtime?.OnLateUpdate();
    internal void OnFrameFixedUpdate() => _runtime?.OnFixedUpdate();
    internal void OnFrameGUI() => _runtime?.OnGUI();

    private void ActivateCompiledProject(string projectPath, string assemblyPath)
    {
        if (_runtime == null || _context == null || !File.Exists(assemblyPath))
            return;

        try
        {
            var count = _runtime.Load(projectPath, assemblyPath, _context);
            Log.LogInfo($"Activated IL2CPP shared script project ({count} entry point(s)).");
        }
        catch (Exception ex)
        {
            Log.LogError($"Could not activate compiled IL2CPP script project: {ex}");
        }
    }

    public override bool Unload()
    {
        try
        {
            _service?.Dispose();
            _runtime?.UnloadAll();
            if (_host != null)
                UObject.Destroy(_host);
        }
        catch (Exception ex)
        {
            Log.LogError($"Error while unloading Custom Script Engine IL2CPP: {ex}");
        }
        finally
        {
            _service = null;
            _runtime = null;
            _host = null;
            _context = null;
            Instance = null;
        }

        return true;
    }
}

/// <summary>
/// Small IL2CPP-injected MonoBehaviour which forwards Unity lifecycle callbacks to
/// the managed script project. BepInEx BasePlugin.AddComponent registers the type.
/// </summary>
public sealed class ScriptEngineHost : MonoBehaviour
{
    public ScriptEngineHost(IntPtr pointer) : base(pointer) { }

    private void Update() => ScriptEnginePlugin.Instance?.OnFrameUpdate();
    private void LateUpdate() => ScriptEnginePlugin.Instance?.OnFrameLateUpdate();
    private void FixedUpdate() => ScriptEnginePlugin.Instance?.OnFrameFixedUpdate();
    private void OnGUI() => ScriptEnginePlugin.Instance?.OnFrameGUI();
}
