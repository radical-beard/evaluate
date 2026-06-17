using System;
using System.Collections.Generic;
using System.Linq;
using YamlDotNet.Serialization;

namespace Evaluate;

// One declared member of a script's returned handle, e.g. `position: get set vec3`.
public sealed class ReturnSpec
{
    public string Name = "";
    public bool CanGet;
    public bool CanSet;
    public string Type = "";

    // The accessor function names the module must expose for this member.
    public IEnumerable<string> RequiredAccessors()
    {
        if (CanGet) yield return $"get_{Name}";
        if (CanSet) yield return $"set_{Name}";
    }
}

// A script's `---` YAML signature plus the Lua body that follows it. The body is
// split off and handed to Lua-CSharp verbatim; the signature is parsed as real
// YAML. (The `returns` access spec — "get set vec3" — is a small grammar parsed
// from the YAML scalar value, not part of YAML itself.)
public sealed class Frontmatter
{
    public List<string> Configs = new();
    public List<string> Apis = new();
    public List<string> Register = new();
    public List<ReturnSpec> Returns = new();
    public List<string> Assets = new();
    public string Body = "";

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    public static Frontmatter Parse(string source)
    {
        var fm = new Frontmatter();
        var lines = source.Replace("\r\n", "\n").Split('\n');

        // A script with no leading `---` block is all body, no declared signature.
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            fm.Body = source;
            return fm;
        }

        int end = -1;
        for (int i = 1; i < lines.Length; i++)
            if (lines[i].Trim() == "---") { end = i; break; }
        if (end < 0) { fm.Body = source; return fm; }   // unterminated; treat as body

        var yamlText = string.Join("\n", lines[1..end]);
        // Keep the body at its original line numbers (pad with blank lines) so Lua
        // runtime errors and stack traces point at the author's real source lines.
        fm.Body = new string('\n', end + 1) + string.Join("\n", lines[(end + 1)..]);

        var root = Yaml.Deserialize<Dictionary<string, object>>(yamlText) ?? new();
        fm.Configs = StringList(root, "config");
        fm.Apis = StringList(root, "apis");
        fm.Register = StringList(root, "register");
        fm.Assets = StringList(root, "assets");

        if (root.TryGetValue("returns", out var r) && r is IEnumerable<object> items)
            foreach (var item in items)
                fm.Returns.Add(ParseReturn(item));

        return fm;
    }

    private static List<string> StringList(Dictionary<string, object> root, string key)
    {
        var list = new List<string>();
        if (root.TryGetValue(key, out var v) && v is IEnumerable<object> seq)
            foreach (var item in seq)
                if (item != null) list.Add(item.ToString()!);
        return list;
    }

    // A returns item is either a scalar (`- spawn`, a plain member) or a
    // single-key map (`- position: get set vec3`, a get/set property).
    private static ReturnSpec ParseReturn(object item)
    {
        if (item is IDictionary<object, object> map && map.Count > 0)
        {
            var kv = map.Cast<KeyValuePair<object, object>>().First();
            return ParseSpec(kv.Key?.ToString() ?? "", kv.Value?.ToString() ?? "");
        }
        return new ReturnSpec { Name = item?.ToString() ?? "" };
    }

    // "get set vec3" -> { CanGet, CanSet, Type = "vec3" }
    private static ReturnSpec ParseSpec(string name, string accessSpec)
    {
        var spec = new ReturnSpec { Name = name };
        foreach (var tok in accessSpec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok == "get") spec.CanGet = true;
            else if (tok == "set") spec.CanSet = true;
            else spec.Type = tok;                            // last non-access token = type
        }
        return spec;
    }
}
