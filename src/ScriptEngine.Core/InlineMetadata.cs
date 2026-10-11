namespace CustomScriptEngine.Core;

public static class InlineMetadata
{
    public static ScriptManifest Apply(string scriptPath, ScriptManifest manifest)
    {
        var result = new ScriptManifest
        {
            Name = manifest.Name,
            Script = manifest.Script,
            References = new List<string>(manifest.References),
            Defines = new List<string>(manifest.Defines),
            AutoReload = manifest.AutoReload
        };

        foreach (var line in File.ReadLines(scriptPath).Take(100))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;

            var directive = trimmed.Substring(2).Trim();
            if (directive.StartsWith("@reference ", StringComparison.OrdinalIgnoreCase))
            {
                var value = directive.Substring(11).Trim();
                if (value.Length > 0 && !result.References.Contains(value, StringComparer.OrdinalIgnoreCase))
                    result.References.Add(value);
            }
            else if (directive.StartsWith("@define ", StringComparison.OrdinalIgnoreCase))
            {
                var value = directive.Substring(8).Trim();
                if (value.Length > 0 && !result.Defines.Contains(value, StringComparer.Ordinal))
                    result.Defines.Add(value);
            }
        }

        return result;
    }
}
