using System.Collections.Generic;

namespace Evaluate;

// One node declared in a scene file: its name, Godot type, optional parent (by
// name), optional `.node.evt` behavior script, and any remaining keys as
// engine properties to set after construction (position, etc.).
public sealed class NodeSpec
{
    public string Name = "";
    public string Type = "";
    public string? Parent;
    public string? Script;
    public Dictionary<string, object> Props = new();
}

// A parsed `*.scene.toml`: an ordered node tree, plus (for the reserved
// `global.scene.toml` manifest) the scene to start in.
public sealed class SceneSpec
{
    public string? StartScene;
    public List<NodeSpec> Nodes = new();
}

// Turns a `*.scene.toml` document into a typed SceneSpec. Structure is a bare
// `start_scene = "..."` (manifest only) plus a sequence of `[[node]]` blocks;
// each block's `name`/`type`/`parent`/`script` are reserved and every other key
// becomes an engine property applied to the instantiated node.
public static class SceneFile
{
    private static readonly HashSet<string> Reserved = new() { "name", "type", "parent", "script" };

    public static SceneSpec Parse(string toml)
    {
        var doc = Toml.ParseDocument(toml);
        var spec = new SceneSpec();

        if (doc.Root.TryGetValue("start_scene", out var ss) && ss is string s)
            spec.StartScene = s;

        if (doc.TableArrays.TryGetValue("node", out var nodes))
            foreach (var n in nodes)
            {
                var node = new NodeSpec
                {
                    Name = Str(n, "name"),
                    Type = Str(n, "type"),
                    Parent = n.TryGetValue("parent", out var p) ? p as string : null,
                    Script = n.TryGetValue("script", out var sc) ? sc as string : null,
                };
                foreach (var kv in n)
                    if (!Reserved.Contains(kv.Key)) node.Props[kv.Key] = kv.Value;
                spec.Nodes.Add(node);
            }

        return spec;
    }

    private static string Str(Dictionary<string, object> d, string key) =>
        d.TryGetValue(key, out var v) && v is string s ? s : "";
}
