using System.Collections.Generic;
using Tomlyn.Model;

namespace Evaluate;

// One node declared in a scene file: its name (the table key), Godot type,
// optional `.node.evt` behavior script, any remaining keys as engine properties,
// and its child nodes. The tree is the table path: `[nodes.Player.Camera]` makes
// Camera a child of Player.
public sealed class NodeSpec
{
    public string Name = "";
    public string Type = "";
    public string? Script;
    public Dictionary<string, object> Props = new();
    public Dictionary<string, object> Params = new();  // `params = {..}` -> the script's `params` global
    public Dictionary<string, object> Meta = new();   // `meta = {..}` -> node.set_meta
    public List<string> Groups = new();               // `groups = [..]` -> node.add_to_group
    public string? Instance;                          // `instance = "scene"` -> that scene's roots become children
    public bool Unique;                               // `unique = true` -> node.unique_name_in_owner (%Name)
    public List<ConnectionSpec> Connections = new();  // `connections = [{signal,to,method}]`
    public List<NodeSpec> Children = new();
}

// One declarative signal wiring: this node's `Signal` -> the `Method` on the node at
// `To` (a path resolved from the scene root). The target method is a Godot method
// (built-in like `hide`/`queue_free`); Lua handlers connect in code via obj:connect.
public sealed class ConnectionSpec
{
    public string Signal = "";
    public string To = "";
    public string Method = "";
}

// A parsed `*.scene` file: a node tree, plus (for the reserved `global.scene`
// manifest) the scene to start in, and an optional free-text `description` (scene
// docs as data — survives editor round-trips, unlike comments).
public sealed class SceneSpec
{
    public string? StartScene;
    public string? Description;
    public List<NodeSpec> Nodes = new();   // root-level nodes
}

// Turns a `*.scene` file (TOML content) into a typed SceneSpec. Structure is a
// bare `start_scene = "..."` (manifest only) plus a `[nodes.*]` tree where each
// node's `type`/`script` are reserved, a sub-table is a child node, and any other
// key is an engine property applied to the instantiated node.
public static class SceneFile
{
    // Characters Godot strips from a Node name (Node.set_name sanitizes these). A
    // declared name containing one would not round-trip, silently breaking the
    // path-based scene.find — so we reject it at parse time instead.
    private static readonly char[] ReservedNameChars = { '.', ':', '@', '/', '%', '"' };

    public static SceneSpec Parse(string toml)
    {
        var model = Toml.Model(toml);
        var spec = new SceneSpec();

        if (model.TryGetValue("start_scene", out var ss) && ss is string s)
            spec.StartScene = s;

        if (model.TryGetValue("description", out var ds) && ds is string d)
            spec.Description = d;

        if (model.TryGetValue("nodes", out var n) && n is TomlTable nodes)
            foreach (var kv in nodes)
                if (kv.Value is TomlTable table)
                    spec.Nodes.Add(ParseNode(kv.Key, table));

        return spec;
    }

    // A node's name is its table key. `meta` (a table), `params` (a table) and `groups`
    // (an array) are reserved STRUCTURAL keys (so a node can't be named those); otherwise a
    // sub-table is ALWAYS a child node (even one keyed `type`/`script`); `type`/`script`
    // scalars are reserved; everything else is an engine property. Every node needs a
    // `type` (except a pure `instance=` node — not yet).
    private static NodeSpec ParseNode(string name, TomlTable table)
    {
        if (name.IndexOfAny(ReservedNameChars) >= 0)
            throw new EvaluateException(
                $"scene node name '{name}' contains a reserved character (any of . : @ / % \"); " +
                "Godot would rewrite it and break path lookup");

        var node = new NodeSpec { Name = name };
        foreach (var kv in table)
        {
            // Reserved structural keys take precedence over the sub-table=child rule.
            if (kv.Key == "meta")
            {
                if (kv.Value is TomlTable mt)
                    foreach (var mk in mt) node.Meta[mk.Key] = Toml.FromToml(mk.Value);
                else
                    throw new EvaluateException($"scene node '{name}': 'meta' must be a table of key = value");
                continue;
            }
            if (kv.Key == "groups")
            {
                if (kv.Value is TomlArray ga)
                    foreach (var g in ga) node.Groups.Add(g?.ToString() ?? "");
                else
                    throw new EvaluateException($"scene node '{name}': 'groups' must be an array of strings");
                continue;
            }
            // `params = {..}` (or a `[nodes.X.params]` section) supplies per-instance values
            // for the node's `*.node.evt` script. Validated against the script's declared
            // `params:` at attach time (see Loader); here we just collect the raw values.
            if (kv.Key == "params")
            {
                if (kv.Value is TomlTable pt)
                    foreach (var pk in pt) node.Params[pk.Key] = Toml.FromToml(pk.Value);
                else
                    throw new EvaluateException($"scene node '{name}': 'params' must be a table of key = value");
                continue;
            }
            if (kv.Key == "instance") { node.Instance = kv.Value?.ToString(); continue; }
            if (kv.Key == "unique") { node.Unique = kv.Value is bool ub && ub; continue; }
            if (kv.Key == "connections")
            {
                if (kv.Value is TomlArray ca)
                    foreach (var item in ca)
                        if (item is TomlTable ct)
                            node.Connections.Add(new ConnectionSpec
                            {
                                Signal = ct.TryGetValue("signal", out var sg) ? sg?.ToString() ?? "" : "",
                                To = ct.TryGetValue("to", out var to) ? to?.ToString() ?? "" : "",
                                Method = ct.TryGetValue("method", out var me) ? me?.ToString() ?? "" : "",
                            });
                        else
                            throw new EvaluateException($"scene node '{name}': each 'connections' entry must be a table");
                else
                    throw new EvaluateException($"scene node '{name}': 'connections' must be an array of tables");
                continue;
            }
            if (kv.Value is TomlTable sub)
            {
                // A sub-table marked with `_type` is an inline sub-RESOURCE property (e.g.
                // `mesh = { _type = "BoxMesh", size = [1,1,1] }`), not a child node.
                if (sub.ContainsKey("_type")) { node.Props[kv.Key] = Toml.FromToml(sub); continue; }
                node.Children.Add(ParseNode(kv.Key, sub));
                continue;
            }
            switch (kv.Key)
            {
                case "type": node.Type = kv.Value?.ToString() ?? ""; break;
                case "script": node.Script = kv.Value?.ToString(); break;
                default: node.Props[kv.Key] = Toml.FromToml(kv.Value); break;
            }
        }

        if (string.IsNullOrEmpty(node.Type))
            throw new EvaluateException(
                $"scene node '{name}' has no 'type'. (A sub-table is always a child node; " +
                "vector/struct properties must be lists like [x, y, z], not tables.)");
        return node;
    }
}
