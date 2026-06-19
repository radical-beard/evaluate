using System.Collections.Generic;
using Tomlyn.Model;

namespace Evaluate;

// TOML reader backed by Tomlyn. Two shapes are exposed:
//  - Parse: section -> (key -> value), matching the original hand-rolled parser's
//    contract so the flat config files (game.toml, ...) and their callers are
//    unchanged.
//  - FromToml: the shared value mapper (Tomlyn model -> bool / double / string /
//    object[] / nested Dictionary), used by both Parse and the scene-file parser.
public static class Toml
{
    // Parse to Tomlyn's document model, turning Tomlyn's TomlException into a clean
    // EvaluateException (the hand-rolled parser this replaced never threw, so every
    // caller — config + scene files — must treat a malformed/duplicate-key file as a
    // recoverable error, not an engine crash). Empty/whitespace input is valid TOML
    // and yields an empty table.
    internal static TomlTable Model(string text)
    {
        try
        {
            return Tomlyn.Toml.ToModel(text);
        }
        catch (Tomlyn.TomlException e)
        {
            throw new EvaluateException($"TOML parse error: {e.Message.Replace("\r", " ").Replace("\n", " ")}");
        }
    }

    public static Dictionary<string, Dictionary<string, object>> Parse(string text)
    {
        var model = Model(text);
        var result = new Dictionary<string, Dictionary<string, object>>();
        var root = new Dictionary<string, object>();
        result[""] = root;

        foreach (var kv in model)
        {
            if (kv.Value is TomlTable section)
            {
                var t = new Dictionary<string, object>();
                foreach (var sk in section) t[sk.Key] = FromToml(sk.Value);
                result[kv.Key] = t;
            }
            else
            {
                root[kv.Key] = FromToml(kv.Value);   // bare top-level key
            }
        }
        return result;
    }

    // Map a Tomlyn model value to Evaluate's plain object model. Integers are
    // widened to double (as the old parser did, so config consumers are unchanged);
    // arrays become object[]; sub-tables become nested dictionaries.
    public static object FromToml(object? v) => v switch
    {
        null => "",
        bool b => b,
        long l => (double)l,
        double d => d,
        string s => s,
        TomlArray arr => ArrayFromToml(arr),
        TomlTable tbl => TableFromToml(tbl),
        _ => v.ToString() ?? "",   // dates / other scalars -> string
    };

    private static object[] ArrayFromToml(TomlArray arr)
    {
        var list = new object[arr.Count];
        for (int i = 0; i < arr.Count; i++) list[i] = FromToml(arr[i]);
        return list;
    }

    private static Dictionary<string, object> TableFromToml(TomlTable tbl)
    {
        var d = new Dictionary<string, object>();
        foreach (var kv in tbl) d[kv.Key] = FromToml(kv.Value);
        return d;
    }
}
