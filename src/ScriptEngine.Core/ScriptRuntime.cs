using System.Reflection;

namespace CustomScriptEngine.Core;

/// <summary>
/// Runtime loader for Unity Mono. Assemblies are loaded from bytes into Mono's default AppDomain.
/// Unity Mono does not provide the collectible AssemblyLoadContext model used by .NET 6+ CoreCLR,
/// so true assembly unloading is not available here. Plugin GameObjects and instances are still
/// stopped and destroyed so scripts can be reloaded functionally.
/// </summary>
public sealed class ScriptRuntime
{
    private readonly List<IPluginAdapter> _adapters;
    private readonly Action<string> _log;
    private readonly Dictionary<string, LoadedScript> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ScriptRuntime(IEnumerable<IPluginAdapter> adapters, Action<string> log)
    {
        _adapters = adapters.ToList();
        _log = log;
    }

    public IReadOnlyCollection<LoadedScript> Current
    {
        get { lock (_gate) return _loaded.Values.ToArray(); }
    }

    public LoadedScript Load(string scriptPath, string assemblyPath)
    {
        var fullScript = Path.GetFullPath(scriptPath);
        Unload(fullScript);

        var assembly = Assembly.Load(File.ReadAllBytes(assemblyPath));
        var instances = new List<object>();

        foreach (var type in GetLoadableTypes(assembly))
        {
            var adapter = _adapters.FirstOrDefault(x => x.CanHandle(type));
            if (adapter == null)
                continue;

            try
            {
                var instance = adapter.Create(type);
                adapter.Start(instance);
                instances.Add(instance);
                _log($"Loaded script type: {type.FullName}");
            }
            catch (TargetInvocationException ex)
            {
                var cause = ex.InnerException ?? ex;
                _log($"Failed to start {type.FullName}: {cause}");
            }
            catch (Exception ex)
            {
                _log($"Failed to start {type.FullName}: {ex}");
            }
        }

        var loaded = new LoadedScript
        {
            ScriptPath = fullScript,
            AssemblyPath = assemblyPath,
            Assembly = assembly,
            PluginInstances = instances
        };

        lock (_gate)
            _loaded[fullScript] = loaded;

        return loaded;
    }

    public void Unload(string scriptPath)
    {
        LoadedScript? old;
        var full = Path.GetFullPath(scriptPath);
        lock (_gate)
        {
            if (!_loaded.TryGetValue(full, out old))
                return;
            _loaded.Remove(full);
        }

        Stop(old);
    }

    public void UnloadAll()
    {
        LoadedScript[] scripts;
        lock (_gate)
        {
            scripts = _loaded.Values.ToArray();
            _loaded.Clear();
        }

        foreach (var script in scripts)
            Stop(script);
    }

    private void Stop(LoadedScript script)
    {
        foreach (var adapter in _adapters)
        {
            foreach (var instance in script.PluginInstances.ToArray())
            {
                try
                {
                    if (adapter.CanHandle(instance.GetType()))
                        adapter.Stop(instance);
                }
                catch (Exception ex)
                {
                    _log($"Error stopping {instance.GetType().FullName}: {ex}");
                }
            }
        }

        script.PluginInstances.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }
}
