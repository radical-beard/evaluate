using System;
using System.Collections.Generic;
using Godot;
using Lua;

namespace Evaluate;

// Builds Godot node trees from a parsed scene. Shared by the runtime (live
// instantiation under a layer container) and the editor import plugin (packing a
// PackedScene for preview). Pure runtime types only — no editor dependency.
public static class SceneBuilder
{
    // Build a node and its whole subtree from a spec. `visit` is invoked for each
    // (spec, node) pair after the node and its children exist — the runtime uses
    // it to attach node scripts; the editor passes null.
    public static Node BuildNode(NodeSpec spec, GodotBinder binder, Action<NodeSpec, Node>? visit = null,
        Func<string, IReadOnlyList<NodeSpec>>? resolveScene = null, HashSet<string>? instanceChain = null)
    {
        var obj = binder.Instantiate(spec.Type)
            ?? throw new EvaluateException($"scene node '{spec.Name}' has unknown type '{spec.Type}'");
        if (obj is not Node node)
            throw new EvaluateException($"scene node '{spec.Name}' type '{spec.Type}' is not a Node");

        if (!string.IsNullOrEmpty(spec.Name)) node.Name = spec.Name;
        foreach (var kv in spec.Props) binder.SetProperty(obj, kv.Key, TomlToLua(kv.Value));
        foreach (var kv in spec.Meta) node.SetMeta(kv.Key, TomlToVariant(kv.Value));
        // persistent: true so the group is saved when the node is packed (the editor
        // edit path round-trips through a PackedScene) and shows in the editor Groups tab.
        foreach (var g in spec.Groups) if (!string.IsNullOrEmpty(g)) node.AddToGroup(g, persistent: true);
        if (spec.Unique) node.UniqueNameInOwner = true;

        // Stash the structural facts that aren't native Godot node state (script,
        // instance, the author's original property keys, declarative connections) so
        // SceneWriter can reverse this build back into an equivalent `.scene`.
        // Namespaced under `__evt_` so it never collides with user `meta`, and
        // SceneWriter strips the prefix from what it emits.
        StashRoundtripMeta(node, spec);

        foreach (var child in spec.Children)
            node.AddChild(BuildNode(child, binder, visit, resolveScene, instanceChain));

        // `instance = "scene"`: that scene's root nodes are built as children of this node. A
        // visited-set across the instance chain rejects a self/mutual reference with a clean
        // error instead of recursing into an uncatchable StackOverflow.
        if (spec.Instance is { } inst && resolveScene is not null)
        {
            instanceChain ??= new HashSet<string>();
            if (!instanceChain.Add(inst))
                throw new EvaluateException($"scene instance cycle: '{inst}' is instanced within itself");
            foreach (var rootSpec in resolveScene(inst))
            {
                var child = BuildNode(rootSpec, binder, visit, resolveScene, instanceChain);
                child.SetMeta(MetaInstanced, true);   // SceneWriter: an instanced root, not an inline child
                node.AddChild(child);
            }
            instanceChain.Remove(inst);     // a sibling branch may reuse the same scene (not a cycle)
        }

        visit?.Invoke(spec, node);
        return node;
    }

    // ---- round-trip meta (consumed by SceneWriter) ---------------------------

    // Reserved meta-key namespace for facts SceneWriter needs but a bare Godot node
    // doesn't carry. Never emitted into a `.scene` file as user `meta`.
    public const string MetaPrefix     = "__evt_";
    public const string MetaScript     = MetaPrefix + "script";       // -> `script = "..."`
    public const string MetaInstance   = MetaPrefix + "instance";     // -> `instance = "..."`
    public const string MetaPropKeys   = MetaPrefix + "propkeys";     // author's original prop keys
    public const string MetaParams     = MetaPrefix + "params";       // -> `params = {...}` (node-script instance params)
    public const string MetaConnections = MetaPrefix + "connections"; // -> `connections = [{...}]`
    public const string MetaInstanced  = MetaPrefix + "instanced";    // a root pulled in by `instance=`

    private static void StashRoundtripMeta(Node node, NodeSpec spec)
    {
        if (!string.IsNullOrEmpty(spec.Script)) node.SetMeta(MetaScript, spec.Script);
        if (spec.Instance is { } inst) node.SetMeta(MetaInstance, inst);
        if (spec.Params.Count > 0)
        {
            var d = new Godot.Collections.Dictionary();
            foreach (var kv in spec.Params) d[kv.Key] = TomlToVariant(kv.Value);
            node.SetMeta(MetaParams, d);                 // SceneWriter reverses this back to `params = {..}`
        }
        if (spec.Props.Count > 0)
        {
            var keys = new string[spec.Props.Count];
            spec.Props.Keys.CopyTo(keys, 0);
            node.SetMeta(MetaPropKeys, keys);            // PackedStringArray
        }
        if (spec.Connections.Count > 0)
        {
            var arr = new Godot.Collections.Array();
            foreach (var c in spec.Connections)
                arr.Add(new Godot.Collections.Dictionary
                {
                    ["signal"] = c.Signal, ["to"] = c.To, ["method"] = c.Method,
                });
            node.SetMeta(MetaConnections, arr);
        }
    }

    // Pack a scene's node tree into a PackedScene (editor preview). No scripts run.
    public static PackedScene BuildPackedScene(SceneSpec spec, GodotBinder binder,
        Func<string, IReadOnlyList<NodeSpec>>? resolveScene = null)
    {
        var root = new Node { Name = "Scene" };
        foreach (var n in spec.Nodes) root.AddChild(BuildNode(n, binder, null, resolveScene));
        SetOwner(root, root);
        var packed = new PackedScene();
        packed.Pack(root);
        root.Free();    // Pack captured the tree; the temporary build nodes are done
        return packed;
    }

    // PackedScene.Pack only captures descendants whose Owner is the packed root.
    private static void SetOwner(Node node, Node owner)
    {
        foreach (var child in node.GetChildren())
        {
            child.Owner = owner;
            SetOwner(child, owner);
        }
    }

    // ---- TOML value -> Lua marshalling (shared with config loading) ----------

    // Convert Evaluate's plain TOML object model (bool / double / string / object[])
    // to a Lua value, so scene-node properties and config tables marshal the same way.
    internal static LuaValue TomlToLua(object v) => v switch
    {
        bool b => b,
        double d => d,
        string s => s,
        object[] arr => ArrayToLua(arr),
        Dictionary<string, object> map => DictToLua(map),   // nested/inline tables
        _ => LuaValue.Nil,
    };

    // ---- TOML value -> Godot Variant (for node `meta` via set_meta) ----------

    internal static Variant TomlToVariant(object v) => v switch
    {
        bool b => b,
        double d => d,
        string s => s,
        object[] arr => ArrayToVariant(arr),
        Dictionary<string, object> map => DictToVariant(map),
        _ => new Variant(),
    };

    private static Variant ArrayToVariant(object[] arr)
    {
        var a = new Godot.Collections.Array();
        foreach (var e in arr) a.Add(TomlToVariant(e));
        return a;
    }

    private static Variant DictToVariant(Dictionary<string, object> map)
    {
        var d = new Godot.Collections.Dictionary();
        foreach (var kv in map) d[kv.Key] = TomlToVariant(kv.Value);
        return d;
    }

    private static LuaValue ArrayToLua(object[] arr)
    {
        var list = new LuaTable();
        for (int i = 0; i < arr.Length; i++) list[i + 1] = TomlToLua(arr[i]);   // 1-based, recursive
        return list;
    }

    private static LuaValue DictToLua(Dictionary<string, object> map)
    {
        var t = new LuaTable();
        foreach (var kv in map) t[kv.Key] = TomlToLua(kv.Value);
        return t;
    }
}
