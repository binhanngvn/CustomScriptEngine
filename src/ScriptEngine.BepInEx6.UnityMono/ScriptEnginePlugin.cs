using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using CustomScriptEngine.Core;
using System.Collections.Concurrent;
using System.Reflection;
using UnityEngine;

namespace CustomScriptEngine.BepInEx6.UnityMono;

[BepInPlugin(GUID, NAME, VERSION)]
public sealed class ScriptEnginePlugin : BaseUnityPlugin
{
    public const string GUID = "com.custom.scriptengine.bepinex6.unitymono";
    public const string NAME = "Custom Script Engine";
    public const string VERSION = "3.0.0";

    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private ScriptEngineService? _service;
    private GameObject? _scriptHost;
    private ConfigEntry<KeyCode>? _reloadKey;
    private ConfigEntry<bool>? _watcherEnabled;
    private ConfigEntry<float>? _debounce;
    private ConfigEntry<bool>? _autoDiscoverReferences;

    private void Awake()
    {
        var scripts = Path.Combine(Paths.BepInExRootPath, "scripts");
        var cache = Path.Combine(scripts, ".cache");
        var libs = Path.Combine(scripts, "libs");
        var shared = Path.Combine(scripts, "shared");
        var referenceCache = Path.Combine(scripts, "reference-cache.txt");

        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(libs);
        Directory.CreateDirectory(shared);

        _reloadKey = Config.Bind("General", "ReloadKey", KeyCode.F6, "Recompile and reload the whole shared .cs script project.");
        _watcherEnabled = Config.Bind("General", "EnableFileSystemWatcher", true, "Automatically rebuild the shared script project after source files are saved.");
        _debounce = Config.Bind("General", "AutoReloadDelay", 0.35f, "Debounce delay before compiling after a file change.");
        _autoDiscoverReferences = Config.Bind("General", "AutoDiscoverReferences", true, "At game/plugin startup, scan game Managed + BepInEx core/plugins + scripts/libs and save absolute DLL paths to reference-cache.txt.");

        _scriptHost = new GameObject("CustomScriptEngine_ScriptHost");
        _scriptHost.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(_scriptHost);

        _service = new ScriptEngineService(scripts, cache, _debounce.Value);
        _service.AddReferenceDirectory(Path.Combine(Paths.BepInExRootPath, "core"));
        _service.AddReferenceDirectory(Path.Combine(Paths.BepInExRootPath, "plugins"));
        _service.AddReferenceDirectory(libs);

        _service.Log += message => Logger.LogInfo(message);
        _service.CompilationFinished += result =>
        {
            if (result.Success)
                Logger.LogInfo("Compiled shared script project successfully.");
            else
                Logger.LogError("Compilation failed for the shared script project; previous version remains active.");
        };
        _service.ProcessingError += (path, ex) => Logger.LogError($"{path}: {ex}");
        _service.CompilationSucceeded += (scriptPath, assemblyPath) =>
            _mainThreadActions.Enqueue(() => ActivateCompiledScript(scriptPath, assemblyPath));
        _service.ProjectCleared += projectPath => _mainThreadActions.Enqueue(() => RemoveScript(projectPath));

        var discoveryDirectories = FindReferenceDiscoveryDirectories(Paths.GameRootPath);
        discoveryDirectories.Add(Path.Combine(Paths.BepInExRootPath, "core"));
        discoveryDirectories.Add(Path.Combine(Paths.BepInExRootPath, "plugins"));
        discoveryDirectories.Add(libs);

        // The startup scan is intentional: it creates the reference cache before
        // the first script compile, while later compiles use the cache.
        if (_autoDiscoverReferences.Value)
            _service.ConfigureCachedReferences(true, referenceCache, discoveryDirectories, scanNow: true);
        else
            _service.ConfigureCachedReferences(true, referenceCache, discoveryDirectories, scanNow: false);

        if (_watcherEnabled.Value)
            _service.StartWatcher();

        Logger.LogInfo($"{NAME} {VERSION} loaded.");
        Logger.LogInfo($"Scripts folder: {scripts}");
        Logger.LogInfo($"Reference cache: {referenceCache}");
        Logger.LogInfo("Put .cs files in scripts/ or scripts/shared/. All eligible files compile together, so classes can call helpers in other files.");
        Logger.LogInfo("Changing any source file reloads the whole shared script project. Files in libs/, examples/ and .cache/ are not compiled as source.");

        // Initial scan after the plugin is fully attached to Unity.
        _mainThreadActions.Enqueue(() =>
        {
            _service?.LoadReferenceCacheFromDisk();
            _service?.ReloadAll();
        });
    }

    private static List<string> FindReferenceDiscoveryDirectories(string gameRoot)
    {
        var result = new List<string>();
        try
        {
            foreach (var dataDirectory in Directory.EnumerateDirectories(gameRoot, "*_Data", SearchOption.TopDirectoryOnly))
            {
                var managed = Path.Combine(dataDirectory, "Managed");
                if (Directory.Exists(managed))
                    result.Add(managed);
            }
        }
        catch { }

        return result;
    }

    private void Update()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Logger.LogError($"Main-thread action failed: {ex}"); }
        }

        if (_reloadKey != null && Input.GetKeyDown(_reloadKey.Value))
            _service?.ReloadAll();
    }

    private void ActivateCompiledScript(string scriptPath, string assemblyPath)
    {
        if (_service == null || _scriptHost == null || !File.Exists(assemblyPath))
            return;

        try
        {
            var loaded = CreateRuntimeIfNeeded().Load(scriptPath, assemblyPath);
            Logger.LogInfo($"Reloaded shared script project ({loaded.PluginInstances.Count} plugin instance(s) across all source files).");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to activate {Path.GetFileName(scriptPath)}: {ex}");
        }
    }

    private void RemoveScript(string scriptPath)
    {
        try
        {
            CreateRuntimeIfNeeded().Unload(scriptPath);
            _service?.ForgetScript(scriptPath);
            Logger.LogInfo("Unloaded the shared script project because no .cs source files remain.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to remove {Path.GetFileName(scriptPath)}: {ex}");
        }
    }

    private ScriptRuntime? _runtime;

    private ScriptRuntime CreateRuntimeIfNeeded()
    {
        if (_runtime != null)
            return _runtime;

        _runtime = new ScriptRuntime(
            new[] { new UnityMonoPluginAdapter(_scriptHost!, Logger) },
            message => Logger.LogInfo(message));
        return _runtime;
    }

    private void OnDestroy()
    {
        try
        {
            _service?.Dispose();
            _runtime?.UnloadAll();
            if (_scriptHost != null)
                Destroy(_scriptHost);
        }
        catch { }
    }

    private sealed class UnityMonoPluginAdapter : IPluginAdapter
    {
        private readonly GameObject _host;
        private readonly ManualLogSource _engineLog;

        public UnityMonoPluginAdapter(GameObject host, ManualLogSource engineLog)
        {
            _host = host;
            _engineLog = engineLog;
        }

        public bool CanHandle(Type pluginType)
            => typeof(BaseUnityPlugin).IsAssignableFrom(pluginType) && !pluginType.IsAbstract;

        public object Create(Type pluginType)
            => _host.AddComponent(pluginType);

        public void Start(object instance)
        {
            // AddComponent invokes Unity's normal Awake/OnEnable lifecycle.
        }

        public void Stop(object instance)
        {
            if (instance is Component component)
            {
                try { UnityEngine.Object.Destroy(component); } catch { }
            }
        }
    }
}
