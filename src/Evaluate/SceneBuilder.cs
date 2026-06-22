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
        foreach (var g in spec.Groups) if (!string.IsNullOrEmpty(g)) node.AddToGroup(g);
        if (spec.Unique) node.UniqueNameInOwner = true;

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
                node.AddChild(BuildNode(rootSpec, binder, visit, resolveScene, instanceChain));
            instanceChain.Remove(inst);     // a sibling branch may reuse the same scene (not a cycle)
        }

        visit?.Invoke(spec, node);
        return node;
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
