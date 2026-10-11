using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using System.Reflection;
using System.Text;

namespace CustomScriptEngine.Core;

public sealed class RoslynCompiler
{
    private readonly ReferenceResolver _resolver;

    public RoslynCompiler(ReferenceResolver resolver) => _resolver = resolver;

    public async Task<CompilationResult> CompileAsync(
        string scriptPath,
        ScriptManifest manifest,
        string outputDirectory,
        IEnumerable<string> referenceDirectories,
        IEnumerable<string>? cachedReferenceFiles,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);

        var source = await File.ReadAllTextAsync(scriptPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var sourceText = SourceText.From(source, Encoding.UTF8);

        var parseOptions = new CSharpParseOptions(
            languageVersion: LanguageVersion.Latest,
            documentationMode: DocumentationMode.Parse,
            preprocessorSymbols: manifest.Defines);

        var tree = CSharpSyntaxTree.ParseText(
            sourceText,
            parseOptions,
            scriptPath,
            cancellationToken: cancellationToken);

        var references = new List<string>();
        var diagnostics = new List<ScriptDiagnostic>();
        var missing = new List<string>();

        // 1) All assemblies currently loaded in Mono. This is the cheapest and
        // most reliable way to catch BepInEx, Unity and Assembly-CSharp once the
        // host plugin has fully attached to Unity.
        AddLoadedAssemblyLocations(references);

        // 2) DLLs declared by manifest/inline metadata or found in configured dirs.
        references.AddRange(_resolver.Resolve(manifest.References, referenceDirectories, out missing));

        // 3) Startup-generated cache containing game/BepInEx/libs DLLs.
        foreach (var dll in cachedReferenceFiles ?? Array.Empty<string>())
        {
            if (File.Exists(dll) && IsReferenceCandidate(dll))
                references.Add(Path.GetFullPath(dll));
        }

        // 4) Explicit scripts/libs directory files are always accepted even if
        // they were added after the cache was created.
        foreach (var directory in referenceDirectories.Where(Directory.Exists))
        {
            try
            {
                references.AddRange(Directory.EnumerateFiles(
                    directory,
                    "*.dll",
                    SearchOption.TopDirectoryOnly));
            }
            catch
            {
                // Ignore inaccessible dirs.
            }
        }

        var metadata = new List<MetadataReference>();
        var usableReferencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in references
                     .Where(File.Exists)
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                metadata.Add(MetadataReference.CreateFromFile(path));
                usableReferencePaths.Add(path);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new ScriptDiagnostic(
                    "Warning",
                    "REF001",
                    $"Could not read reference '{path}': {ex.Message}",
                    path,
                    0,
                    0));
            }
        }

        foreach (var missingReference in missing.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(new ScriptDiagnostic(
                "Error",
                "REF002",
                $"Missing reference '{missingReference}'. Put the DLL in scripts/libs, add it to the cache, or declare a valid manifest/inline reference.",
                scriptPath,
                0,
                0));
        }

        // Unity Mono game assemblies are generally based on .NET Framework/Mono.
        // We intentionally use the newest C# syntax Roslyn can parse, but do not
        // emit framework types from the build host into the user's script unless
        // their own references require them.
        var assemblyName = Path.GetFileNameWithoutExtension(scriptPath) + "." + Guid.NewGuid().ToString("N");
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Debug,
            allowUnsafe: true,
            deterministic: false,
            nullableContextOptions: NullableContextOptions.Enable)
            .WithSpecificDiagnosticOptions(
                System.Collections.Immutable.ImmutableDictionary<string, ReportDiagnostic>.Empty
                    .Add("CS1701", ReportDiagnostic.Suppress));

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { tree },
            metadata,
            compilationOptions);

        var baseName = Path.GetFileNameWithoutExtension(scriptPath);
        var token = Guid.NewGuid().ToString("N");
        var dllPath = Path.Combine(outputDirectory, $"{baseName}.{token}.dll");
        var pdbPath = Path.Combine(outputDirectory, $"{baseName}.{token}.pdb");

        await using (var dllStream = new FileStream(dllPath, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var pdbStream = new FileStream(pdbPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var emitResult = compilation.Emit(
                dllStream,
                pdbStream,
                options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
                cancellationToken: cancellationToken);

            diagnostics.AddRange(
                emitResult.Diagnostics
                    .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                    .Select(ToScriptDiagnostic));
        }

        if (diagnostics.Any(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase)))
        {
            TryDelete(dllPath);
            TryDelete(pdbPath);
        }

        return new CompilationResult
        {
            ScriptPath = scriptPath,
            AssemblyPath = dllPath,
            PdbPath = pdbPath,
            Diagnostics = diagnostics
        };
    }

    private static void AddLoadedAssemblyLocations(List<string> references)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (!assembly.IsDynamic &&
                    !string.IsNullOrWhiteSpace(assembly.Location) &&
                    File.Exists(assembly.Location))
                {
                    references.Add(Path.GetFullPath(assembly.Location));
                }
            }
            catch
            {
                // Dynamic/in-memory assemblies can throw on Location.
            }
        }
    }

    private static bool IsReferenceCandidate(string path)
    {
        var name = Path.GetFileName(path);

        // Never feed Roslyn's own implementation assemblies back into a game
        // script unless a script explicitly requests them. Framework references
        // are already supplied by the loaded runtime.
        if (name.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
            return false;

        if (name.Equals("csc.dll", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("vbc.dll", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static ScriptDiagnostic ToScriptDiagnostic(Diagnostic diagnostic)
    {
        var lineSpan = diagnostic.Location.GetLineSpan();
        var line = lineSpan.StartLinePosition.Line + 1;
        var column = lineSpan.StartLinePosition.Character + 1;
        var file = string.IsNullOrWhiteSpace(lineSpan.Path) ? null : lineSpan.Path;
        return new ScriptDiagnostic(
            diagnostic.Severity.ToString(),
            diagnostic.Id,
            diagnostic.GetMessage(),
            file,
            line,
            column);
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
}
