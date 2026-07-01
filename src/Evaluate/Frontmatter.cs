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

// One declared instance parameter of a node script, e.g. `max_health: number = 100`.
// The scene file that attaches the script supplies a value per node; the script body
// reads the resolved set through the `params` global. Each param has a type (used to
// reject ill-typed scene values) and, optionally, a default (so the scene may omit it);
// a param with no default is REQUIRED — the scene must supply it. Type `""` means "any"
// (no type check). The default/values use Evaluate's plain object model
// (double / string / bool / object[] / Dictionary), the same model TOML produces.
public sealed class ParamSpec
{
    public string Name = "";
    public string Type = "";        // number | string | bool | list | table | "" (any)
    public bool HasDefault;
    public object? Default;

    public bool Required => !HasDefault;
}

// One `require:` binding, e.g. `base: "lib/control/controllable.evt"`. Binds a
// sandbox-local name to the module a path resolves to, so composition is declared
// in the signature instead of restated as `local x = require("…")` at the top of
// the body. The path is resolved by the SAME custom `require` the body could call
// (cache + `returns`-narrowing), so a frontmatter binding is exactly what an inline
// `require(path)` would return — just bound to a name up front.
public sealed class RequireSpec
{
    public string Name = "";
    public string Path = "";
}

// A script's `---` YAML signature plus the Lua body that follows it. The body is
// split off and handed to Lua-CSharp verbatim; the signature is parsed as real
// YAML. (The `returns` access spec — "get set vec3" — and the `params` spec —
// "number = 100" — are small grammars parsed from the YAML scalar value, not part
// of YAML itself.)
public sealed class Frontmatter
{
    public List<string> Configs = new();
    public List<string> Apis = new();
    public List<string> Register = new();
    public List<ReturnSpec> Returns = new();
    public List<ParamSpec> Params = new();
    public List<RequireSpec> Requires = new();
    public List<string> Assets = new();
    // Scenes this system participates in. Empty = global (runs in every scene).
    // Only meaningful for system `.evt` scripts; node scripts get their scene
    // membership implicitly from the scene file that references them.
    public List<string> Scenes = new();
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
        fm.Scenes = StringList(root, "scenes");

        if (root.TryGetValue("returns", out var r) && r is IEnumerable<object> items)
            foreach (var item in items)
                fm.Returns.Add(ParseReturn(item));

        // `params:` is a YAML MAP (name -> spec), not a list: order is irrelevant and
        // names are unique, so a map reads better than a list of single-key maps.
        if (root.TryGetValue("params", out var p) && p is IDictionary<object, object> pmap)
            foreach (var kv in pmap)
                fm.Params.Add(ParseParam(kv.Key?.ToString() ?? "", kv.Value));

        if (root.TryGetValue("require", out var rq))
            ParseRequires(rq, fm.Requires);

        return fm;
    }

    // `require:` binds names to module paths. Accepts either a YAML map (like
    // `params:` — names are unique and order is irrelevant, so a map reads best):
    //   require:
    //     base: "lib/control/controllable.evt"
    //     locomotion: "player/locomotion.evt"
    // or a sequence of single-key maps (mirrors `returns:`):
    //   require:
    //    - base: "lib/control/controllable.evt"
    //    - locomotion: "player/locomotion.evt"
    // Both express the same name -> path bindings. Errors are raised here, at parse
    // time — the same signature-is-the-contract stance as the rest of the frontmatter.
    private static void ParseRequires(object? node, List<RequireSpec> into)
    {
        void Add(string name, object? path)
        {
            var p = path?.ToString() ?? "";
            if (name.Length == 0 || p.Length == 0)
                throw new EvaluateException(
                    $"require: each entry binds a name to a module path (got '{name}: {p}')");
            if (into.Any(r => r.Name == name))
                throw new EvaluateException($"require: duplicate binding '{name}'");
            into.Add(new RequireSpec { Name = name, Path = p });
        }

        switch (node)
        {
            case IDictionary<object, object> map:
                foreach (var kv in map) Add(kv.Key?.ToString() ?? "", kv.Value);
                break;
            case IEnumerable<object> seq:
                foreach (var item in seq)
                {
                    if (item is IDictionary<object, object> m && m.Count == 1)
                    {
                        var kv = m.Cast<KeyValuePair<object, object>>().First();
                        Add(kv.Key?.ToString() ?? "", kv.Value);
                    }
                    else
                        throw new EvaluateException(
                            "require: list entries must each be a single `name: \"path\"` mapping");
                }
                break;
            default:
                throw new EvaluateException(
                    "require: expected a map or a list of `name: \"path\"` bindings");
        }
    }

    // The param type tokens the grammar recognizes. Anything else in type position is
    // an error (caught here, at parse time, not deep inside a hook).
    private static readonly HashSet<string> TypeTokens =
        new() { "number", "string", "bool", "list", "table", "any" };

    // Parse one `name: <spec>` entry. The YAML value is either a scalar string carrying
    // the `<type> [= default]` grammar (`number`, `100`, `number = 5`, `"neutral"`), or a
    // YAML sequence/mapping that is itself a list/table default.
    private static ParamSpec ParseParam(string name, object? value) => value switch
    {
        // YamlDotNet's untyped deserializer gives strings for scalars and
        // List/Dictionary for sequences/mappings (so a number reads as the string "100").
        string s => ParseParamSpec(name, s),
        IDictionary<object, object> map => new ParamSpec
            { Name = name, Type = "table", HasDefault = true, Default = DefaultFromYaml(map) },
        IEnumerable<object> seq => new ParamSpec
            { Name = name, Type = "list", HasDefault = true, Default = DefaultFromYaml(seq) },
        // `name:` with an empty value -> an untyped, required param.
        null => new ParamSpec { Name = name },
        _ => new ParamSpec { Name = name, HasDefault = true, Default = value.ToString() },
    };

    // The `<type> [= default]` grammar on a scalar string:
    //   "number"          -> typed, REQUIRED (no default)
    //   "number = 100"    -> typed, default 100
    //   "= 100" / "100"   -> default 100, type inferred (number)
    //   "neutral"         -> default "neutral" (a bare word is a string literal default)
    private static ParamSpec ParseParamSpec(string name, string raw)
    {
        var spec = new ParamSpec { Name = name };
        int eq = raw.IndexOf('=');
        if (eq >= 0)
        {
            var typePart = raw[..eq].Trim();
            spec.Default = ParseLiteral(raw[(eq + 1)..].Trim());
            spec.HasDefault = true;
            spec.Type = typePart.Length == 0 ? InferType(spec.Default) : RequireType(name, typePart);
            return spec;
        }

        var token = raw.Trim();
        if (TypeTokens.Contains(token)) { spec.Type = NormalizeType(token); return spec; }   // typed, required

        spec.Default = ParseLiteral(token);     // a bare scalar is a default; type inferred
        spec.HasDefault = true;
        spec.Type = InferType(spec.Default);
        return spec;
    }

    private static string RequireType(string name, string token)
    {
        if (!TypeTokens.Contains(token))
            throw new EvaluateException(
                $"param '{name}': unknown type '{token}' (valid: {string.Join(", ", TypeTokens)})");
        return NormalizeType(token);
    }

    private static string NormalizeType(string token) => token == "any" ? "" : token;

    // A scalar literal default: bool / number / "quoted" string / bare string.
    private static object ParseLiteral(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s[1..^1];   // quoted -> verbatim string
        if (s == "true") return true;
        if (s == "false") return false;
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return s;
    }

    // A YAML default value (sequence/mapping) -> Evaluate's plain object model, coercing
    // each scalar through ParseLiteral so a numeric/bool list default isn't left as strings.
    private static object DefaultFromYaml(object? v) => v switch
    {
        string s => ParseLiteral(s),
        IDictionary<object, object> map => map.ToDictionary(
            kv => kv.Key?.ToString() ?? "", kv => DefaultFromYaml(kv.Value)),
        IEnumerable<object> seq => seq.Select(DefaultFromYaml).ToArray(),
        null => "",
        _ => v.ToString() ?? "",
    };

    private static string InferType(object? def) => def switch
    {
        bool => "bool",
        double => "number",
        string => "string",
        object[] => "list",
        IDictionary<string, object> => "table",
        _ => "",
    };

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
