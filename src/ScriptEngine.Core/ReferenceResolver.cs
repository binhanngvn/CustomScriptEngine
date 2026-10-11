using System.Reflection;

namespace CustomScriptEngine.Core;

public sealed class ReferenceResolver
{
    private readonly List<string> _searchDirectories = new();

    public IReadOnlyList<string> SearchDirectories => _searchDirectories;

    public void AddDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        var full = Path.GetFullPath(path);
        if (!_searchDirectories.Contains(full, StringComparer.OrdinalIgnoreCase))
            _searchDirectories.Add(full);
    }

    public IReadOnlyList<string> Resolve(
        IEnumerable<string> requestedReferences,
        IEnumerable<string> extraDirectories,
        out List<string> missing)
    {
        var dirs = _searchDirectories
            .Concat(extraDirectories)
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<string>();
        missing = new List<string>();

        foreach (var reference in requestedReferences
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = ResolveOne(reference, dirs);
            if (found == null)
                missing.Add(reference);
            else if (!result.Contains(found, StringComparer.OrdinalIgnoreCase))
                result.Add(found);
        }

        return result;
    }

    public IReadOnlyList<string> DiscoverDlls(IEnumerable<string> directories, bool recursePlugins = true)
    {
        var result = new List<string>();

        foreach (var directory in directories
                     .Where(Directory.Exists)
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var looksLikePlugins = directory.EndsWith(
                    Path.Combine("BepInEx", "plugins"),
                    StringComparison.OrdinalIgnoreCase);

                var option = recursePlugins && looksLikePlugins
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;

                result.AddRange(Directory.EnumerateFiles(directory, "*.dll", option));
            }
            catch
            {
                // Continue; the host will log the directory it could not scan.
            }
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ResolveOne(string reference, IEnumerable<string> directories)
    {
        if (Path.IsPathRooted(reference) && File.Exists(reference))
            return Path.GetFullPath(reference);

        var name = Path.GetFileNameWithoutExtension(reference);
        var fileName = reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? reference
            : reference + ".dll";

        foreach (var directory in directories)
        {
            var direct = Path.Combine(directory, fileName);
            if (File.Exists(direct))
                return Path.GetFullPath(direct);

            try
            {
                var match = Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(p => string.Equals(
                        Path.GetFileNameWithoutExtension(p),
                        name,
                        StringComparison.OrdinalIgnoreCase));

                if (match != null)
                    return Path.GetFullPath(match);
            }
            catch
            {
                // Continue with the next directory.
            }
        }

        try
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .FirstOrDefault(a => string.Equals(
                    a.GetName().Name,
                    name,
                    StringComparison.OrdinalIgnoreCase));

            if (loaded != null && !string.IsNullOrWhiteSpace(loaded.Location) && File.Exists(loaded.Location))
                return Path.GetFullPath(loaded.Location);
        }
        catch
        {
            // Ignore inaccessible dynamic/loading metadata.
        }

        return null;
    }
}
