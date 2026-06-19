using System.Collections.Generic;
using System.Globalization;

namespace Evaluate;

// A parsed TOML document that preserves top-level bare keys, single `[section]`
// tables, and repeated `[[name]]` array-of-tables blocks in order. Used by scene
// files, which declare `start_scene = "..."` plus a sequence of `[[node]]` blocks.
public sealed class TomlDocument
{
    // Bare `key = value` pairs before any section header.
    public Dictionary<string, object> Root = new();
    // `[section]` -> its key/value map.
    public Dictionary<string, Dictionary<string, object>> Tables = new();
    // `[[name]]` -> the blocks in declaration order.
    public Dictionary<string, List<Dictionary<string, object>>> TableArrays = new();
}

// TOML reader for the config subset Evaluate needs: [section] headers, and
// `key = value` where value is a string ("..."), bool (true/false), number, or
// an array [...] of those (nested arrays allowed). Comments with '#' (ignored
// inside strings). Returns section -> (key -> value); value is bool, double,
// string, or object[].
public static class Toml
{
    public static Dictionary<string, Dictionary<string, object>> Parse(string text)
    {
        var result = new Dictionary<string, Dictionary<string, object>>();
        var current = new Dictionary<string, object>();
        result[""] = current;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                current = new Dictionary<string, object>();
                result[line[1..^1].Trim()] = current;
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            current[line[..eq].Trim()] = ParseValue(line[(eq + 1)..].Trim());
        }
        return result;
    }

    // Document-preserving parse: keeps bare top-level keys and, crucially,
    // repeated `[[name]]` blocks (array-of-tables) that the flat Parse() above
    // would collapse to a single section. Same value grammar as Parse().
    public static TomlDocument ParseDocument(string text)
    {
        var doc = new TomlDocument();
        var current = doc.Root;       // bare keys land at the document root

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[[") && line.EndsWith("]]"))
            {
                var name = line[2..^2].Trim();
                current = new Dictionary<string, object>();
                if (!doc.TableArrays.TryGetValue(name, out var list))
                    doc.TableArrays[name] = list = new List<Dictionary<string, object>>();
                list.Add(current);
                continue;
            }
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                current = new Dictionary<string, object>();
                doc.Tables[line[1..^1].Trim()] = current;
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            current[line[..eq].Trim()] = ParseValue(line[(eq + 1)..].Trim());
        }
        return doc;
    }

    private static object ParseValue(string v)
    {
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            return v[1..^1].Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t");
        if (v == "true") return true;
        if (v == "false") return false;
        if (v.StartsWith("[") && v.EndsWith("]"))
        {
            var items = new List<object>();
            foreach (var part in SplitTopLevel(v[1..^1]))
            {
                var t = part.Trim();
                if (t.Length > 0) items.Add(ParseValue(t));
            }
            return items.ToArray();
        }
        return double.Parse(v, CultureInfo.InvariantCulture);
    }

    // Split on top-level commas, ignoring commas inside strings or nested [].
    private static IEnumerable<string> SplitTopLevel(string s)
    {
        int depth = 0, start = 0;
        bool inStr = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"' && (i == 0 || s[i - 1] != '\\')) inStr = !inStr;
            else if (!inStr && c == '[') depth++;
            else if (!inStr && c == ']') depth--;
            else if (!inStr && c == ',' && depth == 0) { yield return s[start..i]; start = i + 1; }
        }
        if (start < s.Length) yield return s[start..];
    }

    private static string StripComment(string line)
    {
        bool inStr = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) inStr = !inStr;
            else if (line[i] == '#' && !inStr) return line[..i];
        }
        return line;
    }
}
