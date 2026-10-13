namespace CustomScriptEngine.Core;

/// <summary>
/// Manages one shared C# script project rooted at BepInEx/scripts.
/// Every eligible .cs file is compiled together into one assembly, which lets
/// source files call each other's types without manual DLL references.
/// </summary>
public sealed class ScriptEngineService : IDisposable
{
    private readonly RoslynCompiler _compiler;
    private readonly string _scriptDirectory;
    private readonly string _cacheDirectory;
    private readonly List<string> _referenceDirectories = new();
    private long _requestVersion;
    private readonly Dictionary<string, string> _lastCompiledAssembly = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _reloadLock = new();
    private readonly double _debounceSeconds;
    private IReadOnlyList<string> _cachedReferenceFiles = Array.Empty<string>();
    private string? _referenceCachePath;
    private List<string> _discoveryDirectories = new();
    private readonly string _projectKeyPath;

    public event Action<CompilationResult>? CompilationFinished;
    public event Action<string, string>? CompilationSucceeded;
    public event Action<string>? Deleted;
    public event Action<string>? ProjectCleared;
    public event Action<string, Exception>? ProcessingError;
    public event Action<string>? Log;

    public bool Watching { get; private set; }
    public IReadOnlyList<string> CachedReferenceFiles => _cachedReferenceFiles;
    /// <summary>Stable logical key used for the combined script project (not a physical source file).</summary>
    public string ProjectKeyPath => _projectKeyPath;

    public ScriptEngineService(string scriptDirectory, string cacheDirectory, double debounceSeconds = 0.35)
    {
        _scriptDirectory = Path.GetFullPath(scriptDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _projectKeyPath = Path.Combine(_cacheDirectory, "__SharedScriptProject__.cs");
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

    /// <summary>
    /// Lists source files compiled into the shared project. shared/ and other source
    /// folders are included. Internal/cache/library/example folders are excluded.
    /// </summary>
    public IReadOnlyList<string> GetScripts()
    {
        try
        {
            return Directory.EnumerateFiles(_scriptDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(IsCompilableSource)
                .Select(Path.GetFullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            // Fail closed: a scan failure must not be mistaken for an empty project,
            // otherwise the currently active script project could be unloaded.
            WriteLog($"Could not enumerate scripts; keeping the current project active: {ex.Message}");
            throw;
        }
    }

    private bool IsCompilableSource(string path)
    {
        var full = Path.GetFullPath(path);
        if (IsInsideCache(full) || !IsUnderScriptDirectory(full))
            return false;

        var relative = Path.GetRelativePath(_scriptDirectory, full);
        var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments.Take(Math.Max(0, segments.Length - 1)))
        {
            if (segment.Equals("libs", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("examples", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private bool IsUnderScriptDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var root = _scriptDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
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
        WriteLog("File watcher enabled. Any eligible .cs file rebuilds the shared script project.");
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

    public void ReloadAll() => RequestProjectCompile();

    /// <summary>Request a shared-project rebuild after any source file changes.</summary>
    public void Reload(string scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
            return;

        var full = Path.GetFullPath(scriptPath);
        if (!IsUnderScriptDirectory(full) || !string.Equals(Path.GetExtension(full), ".cs", StringComparison.OrdinalIgnoreCase))
            return;

        // A script is no longer a separately compiled unit; every source file shares one assembly.
        RequestProjectCompile();
    }

    public bool TryGetLastCompiledAssembly(string scriptPath, out string? assemblyPath)
    {
        var key = NormalizeProjectKey(scriptPath);
        lock (_reloadLock)
            return _lastCompiledAssembly.TryGetValue(key, out assemblyPath);
    }

    public void ForgetScript(string scriptPath)
    {
        var key = NormalizeProjectKey(scriptPath);
        lock (_reloadLock)
            _lastCompiledAssembly.Remove(key);
    }

    private string NormalizeProjectKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return _projectKeyPath;

        var full = Path.GetFullPath(path);
        return string.Equals(full, _projectKeyPath, StringComparison.OrdinalIgnoreCase) || IsUnderScriptDirectory(full)
            ? _projectKeyPath
            : full;
    }

    private void RequestProjectCompile()
    {
        long requestVersion;
        lock (_reloadLock)
            requestVersion = ++_requestVersion;

        _ = CompileProjectInBackgroundAsync(requestVersion, _lifetime.Token);
    }

    private async Task CompileProjectInBackgroundAsync(long requestVersion, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_debounceSeconds), cancellationToken).ConfigureAwait(false);

            lock (_reloadLock)
            {
                if (_requestVersion != requestVersion)
                    return;
            }

            var scriptPaths = GetScripts();
            if (scriptPaths.Count == 0)
            {
                string? previous;
                lock (_reloadLock)
                {
                    if (_requestVersion != requestVersion)
                        return;
                    _lastCompiledAssembly.TryGetValue(_projectKeyPath, out previous);
                    _lastCompiledAssembly.Remove(_projectKeyPath);
                }

                if (!string.IsNullOrWhiteSpace(previous))
                {
                    TryDelete(previous);
                    TryDelete(Path.ChangeExtension(previous, ".pdb"));
                }

                ProjectCleared?.Invoke(_projectKeyPath);
                WriteLog("No .cs source files remain; the shared script project was cleared.");
                return;
            }

            var sourceFiles = new List<ScriptSourceFile>(scriptPaths.Count);
            foreach (var path in scriptPaths)
            {
                var manifest = InlineMetadata.Apply(path, ManifestReader.LoadForScript(path));
                sourceFiles.Add(new ScriptSourceFile { Path = path, Manifest = manifest });
            }

            var result = await _compiler.CompileProjectAsync(
                sourceFiles,
                _projectKeyPath,
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
                WriteLog($"Compilation failed for the shared script project ({scriptPaths.Count} source file(s)). The previous version remains active.");
                return;
            }

            lock (_reloadLock)
            {
                if (_requestVersion != requestVersion)
                {
                    TryDelete(result.AssemblyPath);
                    TryDelete(result.PdbPath);
                    return;
                }

                if (_lastCompiledAssembly.TryGetValue(_projectKeyPath, out var previousPath) &&
                    !string.Equals(previousPath, result.AssemblyPath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(previousPath);
                    TryDelete(Path.ChangeExtension(previousPath, ".pdb"));
                }

                _lastCompiledAssembly[_projectKeyPath] = result.AssemblyPath;
            }

            WriteLog($"Compiled {scriptPaths.Count} source file(s) into one shared runtime assembly.");
            CompilationSucceeded?.Invoke(_projectKeyPath, result.AssemblyPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ProcessingError?.Invoke(_projectKeyPath, ex);
            WriteLog($"Shared script project processing failed: {ex}");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (IsInsideCache(e.FullPath) || !IsUnderScriptDirectory(Path.GetFullPath(e.FullPath)))
            return;

        var ext = Path.GetExtension(e.FullPath);
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            if (IsCompilableSource(e.FullPath))
                RequestProjectCompile();
            return;
        }

        var name = Path.GetFileName(e.FullPath);
        if (name.EndsWith(".script.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("script.json", StringComparison.OrdinalIgnoreCase))
        {
            RequestProjectCompile();
            return;
        }

        if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            ReloadAll();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        OnFileChanged(sender, e);
        if (e.OldFullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || e.OldFullPath.EndsWith(".script.json", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(e.OldFullPath).Equals("script.json", StringComparison.OrdinalIgnoreCase))
        {
            RequestProjectCompile();
        }
    }

    private bool IsInsideCache(string path)
    {
        var full = Path.GetFullPath(path);
        var cache = Path.GetFullPath(_cacheDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(cache, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
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
