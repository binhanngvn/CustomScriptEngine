using System.Reflection;

namespace CustomScriptEngine.Core;

public sealed class ScriptManifest
{
    public string? Name { get; set; }
    public string? Script { get; set; }
    public List<string> References { get; set; } = new();
    public List<string> Defines { get; set; } = new();
    public bool? AutoReload { get; set; }
}

public sealed class ScriptDiagnostic
{
    public string Severity { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }

    public ScriptDiagnostic() { }

    public ScriptDiagnostic(string severity, string id, string message, string? filePath, int line, int column)
    {
        Severity = severity;
        Id = id;
        Message = message;
        FilePath = filePath;
        Line = line;
        Column = column;
    }
}

public sealed class CompilationResult
{
    public string ScriptPath { get; set; } = string.Empty;
    public string AssemblyPath { get; set; } = string.Empty;
    public string PdbPath { get; set; } = string.Empty;
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; set; } = Array.Empty<ScriptDiagnostic>();
    public bool Success => Diagnostics.All(x => !string.Equals(x.Severity, "Error", StringComparison.OrdinalIgnoreCase));
}

public interface IPluginAdapter
{
    bool CanHandle(Type pluginType);
    object Create(Type pluginType);
    void Start(object instance);
    void Stop(object instance);
}

public sealed class LoadedScript
{
    public string ScriptPath { get; set; } = string.Empty;
    public string AssemblyPath { get; set; } = string.Empty;
    public Assembly Assembly { get; set; } = null!;
    public List<object> PluginInstances { get; set; } = new();
}
