using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using System.Reflection;
using System.Text;

namespace CustomScriptEngine.Core;

/// <summary>
/// Compiles a set of .cs files into one runtime assembly. Compiling source files
/// together lets scripts call shared helper classes in other files without manually
/// compiling/referencing one DLL per script.
/// </summary>
public sealed class RoslynCompiler
{
    private readonly ReferenceResolver _resolver;

    public RoslynCompiler(ReferenceResolver resolver) => _resolver = resolver;

    /// <summary>Compatibility overload for callers that only need to compile one source file.</summary>
    public Task<CompilationResult> CompileAsync(
        string scriptPath,
        ScriptManifest manifest,
        string outputDirectory,
        IEnumerable<string> referenceDirectories,
        IEnumerable<string>? cachedReferenceFiles,
        CancellationToken cancellationToken)
    {
        return CompileProjectAsync(
            new[] { new ScriptSourceFile { Path = scriptPath, Manifest = manifest } },
            scriptPath,
            outputDirectory,
            referenceDirectories,
            cachedReferenceFiles,
            cancellationToken);
    }

    public async Task<CompilationResult> CompileProjectAsync(
        IReadOnlyList<ScriptSourceFile> sourceFiles,
        string projectPath,
        string outputDirectory,
        IEnumerable<string> referenceDirectories,
        IEnumerable<string>? cachedReferenceFiles,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);

        var diagnostics = new List<ScriptDiagnostic>();
        var syntaxTrees = new List<SyntaxTree>();
        var usableSources = sourceFiles
            .Where(s => !string.IsNullOrWhiteSpace(s.Path) && File.Exists(s.Path))
            .OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (usableSources.Count == 0)
        {
            diagnostics.Add(new ScriptDiagnostic(
                "Error", "SRC000", "No C# source files were found in the script project.", projectPath, 0, 0));
            return new CompilationResult { ScriptPath = projectPath, Diagnostics = diagnostics };
        }

        // Each file retains its own manifest defines for #if blocks. All manifests'
        // references are combined because every file is emitted into the same assembly.
        foreach (var source in usableSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = source.Manifest ?? new ScriptManifest();
            var parseOptions = new CSharpParseOptions(
                languageVersion: LanguageVersion.Latest,
                documentationMode: DocumentationMode.Parse,
                preprocessorSymbols: manifest.Defines ?? new List<string>());

            var text = await File.ReadAllTextAsync(source.Path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(
                SourceText.From(text, Encoding.UTF8),
                parseOptions,
                Path.GetFullPath(source.Path),
                cancellationToken: cancellationToken));
        }

        var referenceDirectoriesList = referenceDirectories.ToList();
        var references = new List<string>();
        var missing = new List<string>();

        // 1) Assemblies already loaded in the game process.
        AddLoadedAssemblyLocations(references);

        // 2) Merge references from every script manifest/directive.
        var requestedReferences = usableSources
            .SelectMany(s => s.Manifest?.References ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        references.AddRange(_resolver.Resolve(requestedReferences, referenceDirectoriesList, out missing));

        // 3) Startup-generated reference cache.
        foreach (var dll in cachedReferenceFiles ?? Array.Empty<string>())
        {
            if (File.Exists(dll) && IsReferenceCandidate(dll))
                references.Add(Path.GetFullPath(dll));
        }

        // 4) Always discover DLLs added manually after startup.
        foreach (var directory in referenceDirectoriesList.Where(Directory.Exists))
        {
            try
            {
                references.AddRange(Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly));
            }
            catch
            {
                // Ignore inaccessible directories; other reference sources may still work.
            }
        }

        var metadata = new List<MetadataReference>();
        foreach (var path in references
                     .Where(File.Exists)
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                metadata.Add(MetadataReference.CreateFromFile(path));
            }
            catch (Exception ex)
            {
                diagnostics.Add(new ScriptDiagnostic(
                    "Warning", "REF001", $"Could not read reference '{path}': {ex.Message}", path, 0, 0));
            }
        }

        foreach (var missingReference in missing.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(new ScriptDiagnostic(
                "Error",
                "REF002",
                $"Missing reference '{missingReference}'. Put the DLL in scripts/libs, add it to the cache, or declare a valid manifest/inline reference.",
                projectPath,
                0,
                0));
        }

        var assemblyBaseName = Path.GetFileNameWithoutExtension(projectPath);
        if (string.IsNullOrWhiteSpace(assemblyBaseName))
            assemblyBaseName = "CustomScriptProject";

        var assemblyName = assemblyBaseName + "." + Guid.NewGuid().ToString("N");
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
            syntaxTrees,
            metadata,
            compilationOptions);

        var token = Guid.NewGuid().ToString("N");
        var dllPath = Path.Combine(outputDirectory, $"{assemblyBaseName}.{token}.dll");
        var pdbPath = Path.Combine(outputDirectory, $"{assemblyBaseName}.{token}.pdb");

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
            ScriptPath = projectPath,
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
                // Dynamic/in-memory assemblies may throw when Location is queried.
            }
        }
    }

    private static bool IsReferenceCandidate(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
            return false;
        return !name.Equals("csc.dll", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("vbc.dll", StringComparison.OrdinalIgnoreCase);
    }

    private static ScriptDiagnostic ToScriptDiagnostic(Diagnostic diagnostic)
    {
        var lineSpan = diagnostic.Location.GetLineSpan();
        var line = lineSpan.StartLinePosition.Line + 1;
        var column = lineSpan.StartLinePosition.Character + 1;
        var file = string.IsNullOrWhiteSpace(lineSpan.Path) ? null : lineSpan.Path;
        return new ScriptDiagnostic(diagnostic.Severity.ToString(), diagnostic.Id, diagnostic.GetMessage(), file, line, column);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
