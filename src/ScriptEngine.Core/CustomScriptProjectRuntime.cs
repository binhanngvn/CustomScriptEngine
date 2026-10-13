using System.Reflection;

namespace CustomScriptEngine.Core;

/// <summary>
/// Loads and dispatches ordinary managed entry classes compiled from a shared source
/// project. It deliberately does not depend on Unity or BepInEx, allowing a backend
/// adapter to invoke callbacks from its main-thread host.
/// </summary>
public sealed class CustomScriptProjectRuntime
{
    private readonly Action<string> _log;
    private readonly Dictionary<string, LoadedProject> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedCallbackFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public CustomScriptProjectRuntime(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public int EntryCount
    {
        get
        {
            lock (_gate)
                return _projects.Values.Sum(p => p.Entries.Count);
        }
    }

    /// <summary>Replaces the active project with the newly compiled assembly.</summary>
    public int Load(string projectKey, string assemblyPath, ICustomScriptContext context)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            throw new ArgumentException("Project key is required.", nameof(projectKey));
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            throw new FileNotFoundException("Compiled script project assembly was not found.", assemblyPath);
        if (context == null)
            throw new ArgumentNullException(nameof(context));

        var key = Path.GetFullPath(projectKey);
        Unload(key);

        // Unique assembly names are produced by RoslynCompiler for each compilation.
        // On CoreCLR these loaded assemblies stay in the default load context; script
        // callbacks can be stopped, but this class does not promise physical unloading.
        var assembly = Assembly.Load(File.ReadAllBytes(assemblyPath));
        var entries = new List<ICustomScriptEntry>();

        foreach (var type in GetLoadableTypes(assembly))
        {
            if (type == null || type.IsAbstract || type.IsInterface ||
                !typeof(ICustomScriptEntry).IsAssignableFrom(type))
                continue;

            if (type.GetConstructor(Type.EmptyTypes) == null)
            {
                _log($"Skipped script entry '{type.FullName}': add a public parameterless constructor.");
                continue;
            }

            try
            {
                if (Activator.CreateInstance(type) is not ICustomScriptEntry entry)
                    continue;

                // Add before OnLoad so it can receive OnUnload if initialization is partial.
                entries.Add(entry);
                entry.OnLoad(context);
                _log($"Started script entry: {type.FullName}");
            }
            catch (Exception ex)
            {
                _log($"Failed to initialize script entry '{type.FullName}': {Unwrap(ex)}");
                if (entries.Count > 0 && entries[^1].GetType() == type)
                {
                    try { entries[^1].OnUnload(); } catch { }
                    entries.RemoveAt(entries.Count - 1);
                }
            }
        }

        var project = new LoadedProject(key, assemblyPath, assembly, entries);
        lock (_gate)
        {
            _projects[key] = project;
            _loggedCallbackFailures.RemoveWhere(x => x.StartsWith(key + "|", StringComparison.OrdinalIgnoreCase));
        }

        if (entries.Count == 0)
            _log("Project compiled, but no public parameterless ICustomScriptEntry implementation was found. Helper-only projects compile but do not execute by themselves.");

        _log($"Loaded script project with {entries.Count} entry point(s).");
        return entries.Count;
    }

    public void OnUpdate() => Dispatch<ICustomScriptUpdate>("Update", x => x.OnUpdate());
    public void OnLateUpdate() => Dispatch<ICustomScriptLateUpdate>("LateUpdate", x => x.OnLateUpdate());
    public void OnFixedUpdate() => Dispatch<ICustomScriptFixedUpdate>("FixedUpdate", x => x.OnFixedUpdate());
    public void OnGUI() => Dispatch<ICustomScriptOnGUI>("OnGUI", x => x.OnGUI());

    public void Unload(string projectKey)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            return;

        var key = Path.GetFullPath(projectKey);
        LoadedProject? old;
        lock (_gate)
        {
            if (!_projects.TryGetValue(key, out old))
                return;
            _projects.Remove(key);
        }

        for (var i = old.Entries.Count - 1; i >= 0; i--)
        {
            try
            {
                old.Entries[i].OnUnload();
                _log($"Stopped script entry: {old.Entries[i].GetType().FullName}");
            }
            catch (Exception ex)
            {
                _log($"Error stopping script entry '{old.Entries[i].GetType().FullName}': {Unwrap(ex)}");
            }
        }

        lock (_gate)
            _loggedCallbackFailures.RemoveWhere(x => x.StartsWith(key + "|", StringComparison.OrdinalIgnoreCase));
    }

    public void UnloadAll()
    {
        string[] keys;
        lock (_gate)
            keys = _projects.Keys.ToArray();
        foreach (var key in keys)
            Unload(key);
    }

    private void Dispatch<TCallback>(string callbackName, Action<TCallback> callback)
        where TCallback : class
    {
        LoadedProject[] projects;
        lock (_gate)
            projects = _projects.Values.ToArray();

        foreach (var project in projects)
        {
            foreach (var entry in project.Entries)
            {
                if (entry is not TCallback target)
                    continue;

                try
                {
                    callback(target);
                }
                catch (Exception ex)
                {
                    var id = project.Key + "|" + entry.GetType().FullName + "|" + callbackName;
                    var shouldLog = false;
                    lock (_gate)
                        shouldLog = _loggedCallbackFailures.Add(id);

                    if (shouldLog)
                        _log($"Script callback {callbackName} failed in '{entry.GetType().FullName}'. Further errors for this callback are suppressed until reload. Error: {Unwrap(ex)}");
                }
            }
        }
    }

    private static IEnumerable<Type?> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types;
        }
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: not null } tie)
            ex = tie.InnerException;
        return ex;
    }

    private sealed class LoadedProject
    {
        public string Key { get; }
        public string AssemblyPath { get; }
        public Assembly Assembly { get; }
        public List<ICustomScriptEntry> Entries { get; }

        public LoadedProject(string key, string assemblyPath, Assembly assembly, List<ICustomScriptEntry> entries)
        {
            Key = key;
            AssemblyPath = assemblyPath;
            Assembly = assembly;
            Entries = entries;
        }
    }
}
