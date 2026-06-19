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
    public static Node BuildNode(NodeSpec spec, GodotBinder binder, Action<NodeSpec, Node>? visit = null)
    {
        var obj = binder.Instantiate(spec.Type)
            ?? throw new EvaluateException($"scene node '{spec.Name}' has unknown type '{spec.Type}'");
        if (obj is not Node node)
            throw new EvaluateException($"scene node '{spec.Name}' type '{spec.Type}' is not a Node");

        if (!string.IsNullOrEmpty(spec.Name)) node.Name = spec.Name;
        foreach (var kv in spec.Props) binder.SetProperty(obj, kv.Key, TomlToLua(kv.Value));

        foreach (var child in spec.Children)
            node.AddChild(BuildNode(child, binder, visit));

        visit?.Invoke(spec, node);
        return node;
    }

    // Pack a scene's node tree into a PackedScene (editor preview). No scripts run.
    public static PackedScene BuildPackedScene(SceneSpec spec, GodotBinder binder)
    {
        var root = new Node { Name = "Scene" };
        foreach (var n in spec.Nodes) root.AddChild(BuildNode(n, binder));
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
