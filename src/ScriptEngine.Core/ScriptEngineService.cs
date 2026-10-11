namespace CustomScriptEngine.Core;

public sealed class ScriptEngineService : IDisposable
{
    private readonly RoslynCompiler _compiler;
    private readonly string _scriptDirectory;
    private readonly string _cacheDirectory;
    private readonly List<string> _referenceDirectories = new();
    private readonly Dictionary<string, DateTime> _lastReloadRequest = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastCompiledAssembly = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _reloadLock = new();
    private readonly double _debounceSeconds;
    private IReadOnlyList<string> _cachedReferenceFiles = Array.Empty<string>();
    private string? _referenceCachePath;
    private List<string> _discoveryDirectories = new();

    public event Action<CompilationResult>? CompilationFinished;
    public event Action<string, string>? CompilationSucceeded;
    public event Action<string>? Deleted;
    public event Action<string, Exception>? ProcessingError;
    public event Action<string>? Log;

    public bool Watching { get; private set; }
    public IReadOnlyList<string> CachedReferenceFiles => _cachedReferenceFiles;

    public ScriptEngineService(string scriptDirectory, string cacheDirectory, double debounceSeconds = 0.35)
    {
        _scriptDirectory = Path.GetFullPath(scriptDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _debounceSeconds = Math.Max(0.05, debounceSeconds);
        Directory.CreateDirectory(_scriptDirectory);
        Directory.CreateDirectory(_cacheDirectory);
        _compiler = new RoslynCompiler(new ReferenceResolver());
    }

    public void ConfigureCachedReferences(
        bool enabled,
        string cacheFilePath,
        IEnumerable<string> discoveryDirectories,
        bool scanNow = true)
    {
        _referenceCachePath = Path.GetFullPath(cacheFilePath);
        _discoveryDirectories = discoveryDirectories
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!enabled)
        {
            _cachedReferenceFiles = Array.Empty<string>();
            return;
        }

        if (scanNow)
            RefreshReferenceCache();
        else
            _cachedReferenceFiles = ReferenceCache.Load(_referenceCachePath);
    }

    public void RefreshReferenceCache()
    {
        if (string.IsNullOrWhiteSpace(_referenceCachePath))
            return;

        try
        {
            var resolver = new ReferenceResolver();
            var discovered = resolver.DiscoverDlls(_discoveryDirectories, recursePlugins: true)
                .Where(IsSafeReferenceFile)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _cachedReferenceFiles = discovered;
            ReferenceCache.Save(_referenceCachePath, _cachedReferenceFiles);
            WriteLog($"Reference scan complete: cached {_cachedReferenceFiles.Count} DLL(s).");
        }
        catch (Exception ex)
        {
            _cachedReferenceFiles = ReferenceCache.Load(_referenceCachePath);
            WriteLog($"Reference scan failed; using existing cache ({_cachedReferenceFiles.Count} DLL(s)): {ex.Message}");
        }
    }

    private static bool IsSafeReferenceFile(string path)
    {
        var name = Path.GetFileName(path);
        return !name.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("csc.dll", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("vbc.dll", StringComparison.OrdinalIgnoreCase);
    }

    public void LoadReferenceCacheFromDisk()
    {
        if (string.IsNullOrWhiteSpace(_referenceCachePath))
            return;

        _cachedReferenceFiles = ReferenceCache.Load(_referenceCachePath);
        WriteLog($"Loaded {_cachedReferenceFiles.Count} cached DLL reference(s).");
    }

    public void AddReferenceDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        var full = Path.GetFullPath(path);
        if (!_referenceDirectories.Contains(full, StringComparer.OrdinalIgnoreCase))
            _referenceDirectories.Add(full);
    }

    public IReadOnlyList<string> GetScripts()
    {
        return Directory.EnumerateFiles(_scriptDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsInsideCache(p))
            .ToList();
    }

    public void StartWatcher()
    {
        if (Watching)
            return;

        _watcher = new FileSystemWatcher(_scriptDirectory)
        {
            Filter = "*.*",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size |
                           NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        Watching = true;
        WriteLog("File watcher enabled.");
    }

    public void StopWatcher()
    {
        if (_watcher == null)
            return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileChanged;
        _watcher.Created -= OnFileChanged;
        _watcher.Deleted -= OnFileChanged;
        _watcher.Renamed -= OnFileRenamed;
        _watcher.Dispose();
        _watcher = null;
        Watching = false;
    }

    public void ReloadAll()
    {
        foreach (var script in GetScripts())
            RequestCompile(script);
    }

    public void Reload(string scriptPath)
    {
        var full = Path.GetFullPath(scriptPath);
        if (!File.Exists(full) || !string.Equals(Path.GetExtension(full), ".cs", StringComparison.OrdinalIgnoreCase))
            return;
        RequestCompile(full);
    }

    public bool TryGetLastCompiledAssembly(string scriptPath, out string? assemblyPath)
    {
        lock (_reloadLock)
            return _lastCompiledAssembly.TryGetValue(Path.GetFullPath(scriptPath), out assemblyPath);
    }

    public void ForgetScript(string scriptPath)
    {
        var full = Path.GetFullPath(scriptPath);
        lock (_reloadLock)
            _lastCompiledAssembly.Remove(full);
    }

    private void RequestCompile(string scriptPath)
    {
        var full = Path.GetFullPath(scriptPath);
        lock (_reloadLock)
            _lastReloadRequest[full] = DateTime.UtcNow;

        _ = CompileInBackgroundAsync(full, _lifetime.Token);
    }

    private async Task CompileInBackgroundAsync(string scriptPath, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_debounceSeconds), cancellationToken).ConfigureAwait(false);

            DateTime requestTime;
            lock (_reloadLock)
            {
                if (!_lastReloadRequest.TryGetValue(scriptPath, out requestTime))
                    return;
            }

            if (!File.Exists(scriptPath))
                return;

            var manifest = InlineMetadata.Apply(scriptPath, ManifestReader.LoadForScript(scriptPath));
            var result = await _compiler.CompileAsync(
                scriptPath,
                manifest,
                _cacheDirectory,
                _referenceDirectories,
                _cachedReferenceFiles,
                cancellationToken).ConfigureAwait(false);

            CompilationFinished?.Invoke(result);

            foreach (var diagnostic in result.Diagnostics)
            {
                WriteLog($"{diagnostic.Severity} {diagnostic.Id}: {diagnostic.Message} ({diagnostic.FilePath}:{diagnostic.Line}:{diagnostic.Column})");
            }

            if (!result.Success)
            {
                WriteLog($"Compilation failed for {Path.GetFileName(scriptPath)}. The previous version is kept.");
                return;
            }

            lock (_reloadLock)
            {
                if (!_lastReloadRequest.TryGetValue(scriptPath, out var latest) || latest != requestTime)
                {
                    TryDelete(result.AssemblyPath);
                    TryDelete(result.PdbPath);
                    return;
                }

                if (_lastCompiledAssembly.TryGetValue(scriptPath, out var previousPath) &&
                    !string.Equals(previousPath, result.AssemblyPath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(previousPath);
                    TryDelete(Path.ChangeExtension(previousPath, ".pdb"));
                }

                _lastCompiledAssembly[scriptPath] = result.AssemblyPath;
            }

            CompilationSucceeded?.Invoke(scriptPath, result.AssemblyPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ProcessingError?.Invoke(scriptPath, ex);
            WriteLog($"Script processing failed for {scriptPath}: {ex}");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (IsInsideCache(e.FullPath))
            return;

        var ext = Path.GetExtension(e.FullPath);
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            if (e.ChangeType == WatcherChangeTypes.Deleted)
                Deleted?.Invoke(Path.GetFullPath(e.FullPath));
            else
                Reload(e.FullPath);
            return;
        }

        if (e.FullPath.EndsWith(".script.json", StringComparison.OrdinalIgnoreCase))
        {
            var script = e.FullPath.Substring(0, e.FullPath.Length - ".script.json".Length) + ".cs";
            if (File.Exists(script))
                Reload(script);
            return;
        }

        if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            ReloadAll();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (e.OldFullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            Deleted?.Invoke(Path.GetFullPath(e.OldFullPath));
        OnFileChanged(sender, e);
    }

    private bool IsInsideCache(string path)
    {
        var full = Path.GetFullPath(path);
        var cache = Path.GetFullPath(_cacheDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(cache, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private void WriteLog(string message) => Log?.Invoke(message);

    public void Dispose()
    {
        StopWatcher();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
