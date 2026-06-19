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
    public List<NodeSpec> Children = new();
}

// A parsed `*.scene` file: a node tree, plus (for the reserved `global.scene`
// manifest) the scene to start in.
public sealed class SceneSpec
{
    public string? StartScene;
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

        if (model.TryGetValue("nodes", out var n) && n is TomlTable nodes)
            foreach (var kv in nodes)
                if (kv.Value is TomlTable table)
                    spec.Nodes.Add(ParseNode(kv.Key, table));

        return spec;
    }

    // A node's name is its table key; a sub-table is ALWAYS a child node (even one
    // keyed `type`/`script`); `type`/`script` scalars are reserved; everything else
    // is an engine property. Every node must declare a `type`.
    private static NodeSpec ParseNode(string name, TomlTable table)
    {
        if (name.IndexOfAny(ReservedNameChars) >= 0)
            throw new EvaluateException(
                $"scene node name '{name}' contains a reserved character (any of . : @ / % \"); " +
                "Godot would rewrite it and break path lookup");

        var node = new NodeSpec { Name = name };
        foreach (var kv in table)
        {
            if (kv.Value is TomlTable child) { node.Children.Add(ParseNode(kv.Key, child)); continue; }
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
