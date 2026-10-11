using System.Globalization;
using System.Text;

namespace CustomScriptEngine.Core;

/// <summary>
/// Small manifest reader for the Script Engine's simple JSON format.
/// It intentionally avoids System.Text.Json so the Unity Mono plugin does not
/// need the System.Text.Json dependency chain just to read script manifests.
/// Supported fields: name, script, references, defines, autoReload.
/// </summary>
public static class ManifestReader
{
    public static ScriptManifest LoadForScript(string scriptPath)
    {
        var directory = Path.GetDirectoryName(scriptPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(scriptPath);
        var candidates = new[]
        {
            Path.Combine(directory, baseName + ".script.json"),
            Path.Combine(directory, "script.json")
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
                continue;

            try
            {
                var json = File.ReadAllText(candidate, new UTF8Encoding(false, true));
                var manifest = SimpleJsonManifestParser.Parse(json);
                if (string.IsNullOrWhiteSpace(manifest.Script))
                    manifest.Script = Path.GetFileName(scriptPath);
                return manifest;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Could not parse manifest '{candidate}': {ex.Message}", ex);
            }
        }

        return new ScriptManifest { Script = Path.GetFileName(scriptPath) };
    }

    private static class SimpleJsonManifestParser
    {
        public static ScriptManifest Parse(string json)
        {
            var manifest = new ScriptManifest();
            var i = SkipWhitespace(json, 0);
            if (i >= json.Length || json[i] != '{')
                throw new FormatException("Manifest must be a JSON object.");
            i++;

            while (true)
            {
                i = SkipWhitespaceAndComments(json, i);
                if (i >= json.Length)
                    throw new FormatException("Unexpected end of manifest.");
                if (json[i] == '}')
                    break;

                var key = ReadString(json, ref i);
                i = SkipWhitespaceAndComments(json, i);
                Expect(json, ref i, ':');
                i = SkipWhitespaceAndComments(json, i);

                switch (key)
                {
                    case "name":
                        manifest.Name = ReadString(json, ref i);
                        break;
                    case "script":
                        manifest.Script = ReadString(json, ref i);
                        break;
                    case "references":
                        manifest.References = ReadStringArray(json, ref i);
                        break;
                    case "defines":
                        manifest.Defines = ReadStringArray(json, ref i);
                        break;
                    case "autoReload":
                        manifest.AutoReload = ReadNullableBool(json, ref i);
                        break;
                    default:
                        SkipValue(json, ref i);
                        break;
                }

                i = SkipWhitespaceAndComments(json, i);
                if (i >= json.Length)
                    throw new FormatException("Unexpected end of manifest.");
                if (json[i] == ',')
                {
                    i++;
                    continue;
                }
                if (json[i] == '}')
                    break;

                throw new FormatException($"Expected ',' or '}}' at position {i}.");
            }

            return manifest;
        }

        private static List<string> ReadStringArray(string json, ref int i)
        {
            var result = new List<string>();
            i = SkipWhitespaceAndComments(json, i);
            Expect(json, ref i, '[');
            i = SkipWhitespaceAndComments(json, i);

            while (i < json.Length && json[i] != ']')
            {
                result.Add(ReadString(json, ref i));
                i = SkipWhitespaceAndComments(json, i);
                if (i < json.Length && json[i] == ',')
                {
                    i++;
                    i = SkipWhitespaceAndComments(json, i);
                    continue;
                }
                break;
            }

            Expect(json, ref i, ']');
            return result;
        }

        private static bool? ReadNullableBool(string json, ref int i)
        {
            if (StartsWith(json, i, "true"))
            {
                i += 4;
                return true;
            }
            if (StartsWith(json, i, "false"))
            {
                i += 5;
                return false;
            }
            if (StartsWith(json, i, "null"))
            {
                i += 4;
                return null;
            }
            throw new FormatException($"Expected boolean at position {i}.");
        }

        private static string ReadString(string json, ref int i)
        {
            if (i >= json.Length || json[i] != '"')
                throw new FormatException($"Expected string at position {i}.");
            i++;
            var sb = new StringBuilder();

            while (i < json.Length)
            {
                var c = json[i++];
                if (c == '"')
                    return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i >= json.Length)
                    throw new FormatException("Unterminated escape sequence.");

                var e = json[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > json.Length)
                            throw new FormatException("Invalid Unicode escape.");
                        var hex = json.Substring(i, 4);
                        if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            throw new FormatException($"Invalid Unicode escape '\\u{hex}'.");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default:
                        throw new FormatException($"Invalid escape '\\{e}'.");
                }
            }

            throw new FormatException("Unterminated JSON string.");
        }

        private static void SkipValue(string json, ref int i)
        {
            i = SkipWhitespaceAndComments(json, i);
            if (i >= json.Length)
                return;

            if (json[i] == '"')
            {
                _ = ReadString(json, ref i);
                return;
            }

            if (json[i] == '[')
            {
                var depth = 0;
                do
                {
                    if (json[i] == '"')
                        _ = ReadString(json, ref i);
                    else
                    {
                        if (json[i] == '[') depth++;
                        if (json[i] == ']') depth--;
                        i++;
                    }
                } while (i < json.Length && depth > 0);
                return;
            }

            if (json[i] == '{')
            {
                var depth = 0;
                do
                {
                    if (json[i] == '"')
                        _ = ReadString(json, ref i);
                    else
                    {
                        if (json[i] == '{') depth++;
                        if (json[i] == '}') depth--;
                        i++;
                    }
                } while (i < json.Length && depth > 0);
                return;
            }

            while (i < json.Length && json[i] != ',' && json[i] != '}')
                i++;
        }

        private static int SkipWhitespace(string json, int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json[i]))
                i++;
            return i;
        }

        private static int SkipWhitespaceAndComments(string json, int i)
        {
            while (true)
            {
                i = SkipWhitespace(json, i);
                if (i + 1 < json.Length && json[i] == '/' && json[i + 1] == '/')
                {
                    i += 2;
                    while (i < json.Length && json[i] != '\n') i++;
                    continue;
                }
                if (i + 1 < json.Length && json[i] == '/' && json[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < json.Length && !(json[i] == '*' && json[i + 1] == '/')) i++;
                    if (i + 1 >= json.Length) throw new FormatException("Unterminated block comment.");
                    i += 2;
                    continue;
                }
                return i;
            }
        }

        private static void Expect(string json, ref int i, char expected)
        {
            i = SkipWhitespaceAndComments(json, i);
            if (i >= json.Length || json[i] != expected)
                throw new FormatException($"Expected '{expected}' at position {i}.");
            i++;
        }

        private static bool StartsWith(string text, int index, string value)
            => index >= 0 && index + value.Length <= text.Length &&
               string.CompareOrdinal(text, index, value, 0, value.Length) == 0;
    }
}
