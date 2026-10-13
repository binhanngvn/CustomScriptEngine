namespace CustomScriptEngine.Core;

/// <summary>
/// Context passed to user scripts by CustomScriptEngine. The same contract is available
/// to Mono and IL2CPP scripts, so simple helpers can be shared across source files.
/// Unity APIs should only be accessed from lifecycle callbacks on the Unity main thread.
/// </summary>
public interface ICustomScriptContext
{
    string GameRootDirectory { get; }
    string BepInExRootDirectory { get; }
    string ScriptsDirectory { get; }
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message);
}

/// <summary>
/// Entry point for a CustomScriptEngine source project. The engine creates ordinary
/// managed classes implementing this interface; they do not need to be BepInEx plugins.
/// </summary>
public interface ICustomScriptEntry
{
    void OnLoad(ICustomScriptContext context);
    void OnUnload();
}

/// <summary>Optional per-frame callback. Called on Unity's main thread.</summary>
public interface ICustomScriptUpdate
{
    void OnUpdate();
}

/// <summary>Optional late-frame callback. Called on Unity's main thread.</summary>
public interface ICustomScriptLateUpdate
{
    void OnLateUpdate();
}

/// <summary>Optional fixed-step callback. Called on Unity's main thread.</summary>
public interface ICustomScriptFixedUpdate
{
    void OnFixedUpdate();
}

/// <summary>Optional Unity IMGUI callback. Called on Unity's main thread.</summary>
public interface ICustomScriptOnGUI
{
    void OnGUI();
}

/// <summary>Default context implementation shared by the backend-specific loaders.</summary>
public sealed class CustomScriptContext : ICustomScriptContext
{
    private readonly Action<string> _info;
    private readonly Action<string> _warning;
    private readonly Action<string> _error;

    public string GameRootDirectory { get; }
    public string BepInExRootDirectory { get; }
    public string ScriptsDirectory { get; }

    public CustomScriptContext(
        string gameRootDirectory,
        string bepInExRootDirectory,
        string scriptsDirectory,
        Action<string> info,
        Action<string> warning,
        Action<string> error)
    {
        GameRootDirectory = Path.GetFullPath(gameRootDirectory);
        BepInExRootDirectory = Path.GetFullPath(bepInExRootDirectory);
        ScriptsDirectory = Path.GetFullPath(scriptsDirectory);
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _warning = warning ?? throw new ArgumentNullException(nameof(warning));
        _error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public void LogInfo(string message) => _info(message ?? string.Empty);
    public void LogWarning(string message) => _warning(message ?? string.Empty);
    public void LogError(string message) => _error(message ?? string.Empty);
}
