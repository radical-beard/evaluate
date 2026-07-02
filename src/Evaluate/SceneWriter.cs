using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Godot;

namespace Evaluate;

// The inverse of SceneFile + SceneBuilder: serialize a live Godot node tree (as
// edited in the Godot editor) back into a `.scene` file (TOML). Pure runtime types
// only — no editor dependency — so it ships in the package and the editor addon
// (or any tooling) can call it.
//
// Two stages, mirroring the read side's `Parse -> SceneSpec -> BuildNode`:
//   Node tree  -> SceneSpec/NodeSpec   (Builder.ToSpec, the inverse of BuildNode)
//   SceneSpec  -> TOML text            (Emit)
// Splitting at the spec boundary lets round-trip tests assert spec equality cheaply
// before involving the text emitter.
//
// Structural facts a bare node doesn't carry (script, instance, the author's
// original property keys, declarative connections) are recovered from the `__evt_*`
// meta that SceneBuilder.BuildNode stashes; see SceneBuilder.MetaPrefix.
public static class SceneWriter
{
    // Serialize the CHILDREN of a container as the scene's root `[nodes.*]` — the
    // container itself (a scene/global layer wrapper) is never emitted. This is the
    // shape the runtime builds (Loader) and the editor edits (a synthetic root).
    // `header` (optional) carries the scene-LEVEL fields that live outside the node
    // tree — start_scene, controls, scenario, [player], description — so an editor
    // round-trip preserves them verbatim (pass the SceneSpec you parsed).
    public static string WriteContainer(Node container, GodotBinder binder, string? startScene = null,
        string? description = null, SceneSpec? header = null)
    {
        var roots = new List<Node>();
        foreach (var c in container.GetChildren()) roots.Add(c);
        return Write(roots, binder, startScene, description, header);
    }

    // Serialize a set of root nodes (each becomes a top-level `[nodes.X]`).
    public static string Write(IReadOnlyList<Node> roots, GodotBinder binder, string? startScene = null,
        string? description = null, SceneSpec? header = null)
        => Emit(ToScene(roots, binder, startScene, description, header));

    // Serialize one node + subtree as a fragment (tooling / tests).
    public static string WriteNode(Node node, GodotBinder binder)
    {
        var scene = new SceneSpec();
        using var b = new Builder(binder);
        scene.Nodes.Add(b.ToSpec(node));
        return Emit(scene);
    }

    internal static SceneSpec ToScene(IReadOnlyList<Node> roots, GodotBinder binder, string? startScene,
        string? description = null, SceneSpec? header = null)
    {
        var scene = new SceneSpec
        {
            StartScene = startScene ?? header?.StartScene,
            Description = description ?? header?.Description,
            Controls = header?.Controls,
            Scenario = header?.Scenario,
            Player = header?.Player,
        };
        using var b = new Builder(binder);
        foreach (var n in roots) scene.Nodes.Add(b.ToSpec(n));
        return scene;
    }

    // ---- Node tree -> SceneSpec ----------------------------------------------

    // Carries the per-call default-instance cache (one fresh instance per class, to
    // diff properties against) so it can be freed when the walk completes.
    private sealed class Builder : IDisposable
    {
        private readonly GodotBinder _binder;
        private readonly Dictionary<string, GodotObject?> _defaults = new();

        public Builder(GodotBinder binder) => _binder = binder;

        public NodeSpec ToSpec(Node node)
        {
            GuardName(node.Name.ToString());
            var spec = new NodeSpec { Name = node.Name.ToString(), Type = node.GetClass() };

            if (node.HasMeta(SceneBuilder.MetaScript)) spec.Script = node.GetMeta(SceneBuilder.MetaScript).AsString();
            if (node.HasMeta(SceneBuilder.MetaInstance)) spec.Instance = node.GetMeta(SceneBuilder.MetaInstance).AsString();
            if (node.HasMeta(SceneBuilder.MetaBehaviors)) ReadAttachList(node, SceneBuilder.MetaBehaviors, spec.Behaviors);
            if (node.HasMeta(SceneBuilder.MetaMachines)) ReadAttachList(node, SceneBuilder.MetaMachines, spec.Machines);
            spec.Unique = node.UniqueNameInOwner;

            if (node.HasMeta(SceneBuilder.MetaParams))
                foreach (var kv in node.GetMeta(SceneBuilder.MetaParams).AsGodotDictionary())
                {
                    var val = ToToml(kv.Value);
                    if (val is not null) spec.Params[kv.Key.AsString()] = val;
                }

            foreach (StringName g in node.GetGroups())
            {
                var name = g.ToString();
                if (!string.IsNullOrEmpty(name) && !name.StartsWith("_")) spec.Groups.Add(name);
            }

            if (node.HasMeta(SceneBuilder.MetaConnections))
                foreach (var item in node.GetMeta(SceneBuilder.MetaConnections).AsGodotArray())
                {
                    var d = item.AsGodotDictionary();
                    spec.Connections.Add(new ConnectionSpec
                    {
                        Signal = d["signal"].AsString(),
                        To = d["to"].AsString(),
                        Method = d["method"].AsString(),
                    });
                }

            foreach (var meta in node.GetMetaList())
            {
                var key = meta.ToString();
                if (key.StartsWith(SceneBuilder.MetaPrefix)) continue;   // internal stash, never user meta
                var val = ToToml(node.GetMeta(key));
                if (val is not null) spec.Meta[key] = val;
            }

            EmitProps(node, spec);

            // Children, except roots pulled in by `instance=` (they belong to the
            // instanced scene, not this file — re-inlining them would lose the ref).
            foreach (var child in node.GetChildren())
                if (!child.HasMeta(SceneBuilder.MetaInstanced))
                    spec.Children.Add(ToSpec(child));

            return spec;
        }

        private void ReadAttachList(Node node, string metaKey, List<AttachSpec> into)
        {
            foreach (var item in node.GetMeta(metaKey).AsGodotArray())
            {
                var d = item.AsGodotDictionary();
                var spec = new AttachSpec { Script = d["script"].AsString() };
                if (d.ContainsKey("params"))
                    foreach (var kv in d["params"].AsGodotDictionary())
                    {
                        var val = ToToml(kv.Value);
                        if (val is not null) spec.Params[kv.Key.AsString()] = val;
                    }
                into.Add(spec);
            }
        }

        // Decide which of a node's hundreds of properties to write: only those that
        // (a) the author originally wrote (preserved verbatim, so the round-trip is
        // idempotent even for values equal to the type default), or (b) differ from a
        // fresh default instance of the node's class. Spatial transforms are emitted
        // as the clean `position`/`rotation`/`scale` triple rather than a monolithic
        // `transform` table.
        private void EmitProps(Node node, NodeSpec spec)
        {
            var original = new HashSet<string>();
            if (node.HasMeta(SceneBuilder.MetaPropKeys))
                foreach (var k in node.GetMeta(SceneBuilder.MetaPropKeys).AsStringArray())
                    original.Add(k);

            var def = Default(node.GetClass());
            var handled = new HashSet<string>();

            if (node is Node3D or Node2D)
            {
                foreach (var k in TransformFamily) handled.Add(k);
                AddSpatial(node, def, spec, original);
            }

            if (def is null) return;   // unconstructible type: stick to spatial + originals

            foreach (Godot.Collections.Dictionary p in node.GetPropertyList())
            {
                var name = p["name"].AsString();
                // metadata/* is captured via GetMetaList; `_`-prefixed are private. Other
                // `/`-keyed props (e.g. Control `theme_override_colors/font_color`) DO
                // round-trip — they're emitted as quoted keys and set via obj.Set("a/b", v).
                if (handled.Contains(name) || SkipAlways.Contains(name)
                    || name.StartsWith("_") || name.StartsWith("metadata/")) continue;
                handled.Add(name);

                var usage = (PropertyUsageFlags)p["usage"].AsInt64();
                bool storage = (usage & PropertyUsageFlags.Storage) != 0;
                bool keep = original.Contains(name) || (storage && Differs(node.Get(name), def.Get(name)));
                if (!keep) continue;

                var val = ToToml(node.Get(name));
                if (val is not null) spec.Props[name] = val;
            }
        }

        private void AddSpatial(Node node, GodotObject? def, NodeSpec spec, HashSet<string> original)
        {
            if (node is Node3D n3)
            {
                var d = def as Node3D;
                AddVec(spec, original, "position", n3.Position, d?.Position ?? Vector3.Zero);
                AddVec(spec, original, "rotation", n3.Rotation, d?.Rotation ?? Vector3.Zero);
                AddVec(spec, original, "scale", n3.Scale, d?.Scale ?? Vector3.One);
            }
            else if (node is Node2D n2)
            {
                var d = def as Node2D;
                AddVec2(spec, original, "position", n2.Position, d?.Position ?? Vector2.Zero);
                if (original.Contains("rotation") || !Approx(n2.Rotation, d?.Rotation ?? 0f))
                    spec.Props["rotation"] = (double)n2.Rotation;
                AddVec2(spec, original, "scale", n2.Scale, d?.Scale ?? Vector2.One);
            }
        }

        private static void AddVec(NodeSpec spec, HashSet<string> original, string key, Vector3 cur, Vector3 def)
        {
            if (original.Contains(key) || !Approx(cur, def))
                spec.Props[key] = Arr(cur.X, cur.Y, cur.Z);
        }

        private static void AddVec2(NodeSpec spec, HashSet<string> original, string key, Vector2 cur, Vector2 def)
        {
            if (original.Contains(key) || !Approx(cur, def))
                spec.Props[key] = Arr(cur.X, cur.Y);
        }

        private GodotObject? Default(string cls)
        {
            if (!_defaults.TryGetValue(cls, out var o)) _defaults[cls] = o = _binder.Instantiate(cls);
            return o;
        }

        // ---- Variant -> Evaluate's plain TOML object model -------------------
        // (bool / double / string / object[] / Dictionary<string,object>), the same
        // model SceneFile produces, so output marshals back via GodotBinder.ToSetValue.

        private object? ToToml(Variant v)
        {
            switch (v.VariantType)
            {
                case Variant.Type.Nil: return null;
                case Variant.Type.Bool: return v.AsBool();
                case Variant.Type.Int: return (double)v.AsInt64();
                case Variant.Type.Float: return v.AsDouble();
                case Variant.Type.String:
                case Variant.Type.StringName:
                case Variant.Type.NodePath: return v.AsString();

                // All builtin structs (vectors/color/quaternion -> positional arrays;
                // Rect2/Aabb/Basis/Transform*/Projection -> `_type`-tagged tables) go
                // through the generated codec — see the `default:` arm below.

                case Variant.Type.Object: return ResourceToToml(v.AsGodotObject());
                case Variant.Type.Array:
                {
                    var a = v.AsGodotArray();
                    var list = new object[a.Count];
                    for (int i = 0; i < a.Count; i++) list[i] = ToToml(a[i]) ?? "";
                    return list;
                }
                case Variant.Type.Dictionary:
                {
                    var d = v.AsGodotDictionary();
                    var m = new Dictionary<string, object>();
                    foreach (var k in d.Keys) m[k.AsString()] = ToToml(d[k]) ?? "";
                    return m;
                }

                case Variant.Type.PackedByteArray: return Pack(v.AsByteArray(), x => (double)x);
                case Variant.Type.PackedInt32Array: return Pack(v.AsInt32Array(), x => (double)x);
                case Variant.Type.PackedInt64Array: return Pack(v.AsInt64Array(), x => (double)x);
                case Variant.Type.PackedFloat32Array: return Pack(v.AsFloat32Array(), x => (double)x);
                case Variant.Type.PackedFloat64Array: return Pack(v.AsFloat64Array(), x => x);
                case Variant.Type.PackedStringArray: return Pack(v.AsStringArray(), x => (object)x);
                case Variant.Type.PackedVector2Array: return Pack(v.AsVector2Array(), x => (object)Arr(x.X, x.Y));
                case Variant.Type.PackedVector3Array: return Pack(v.AsVector3Array(), x => (object)Arr(x.X, x.Y, x.Z));
                case Variant.Type.PackedVector4Array: return Pack(v.AsVector4Array(), x => (object)Arr(x.X, x.Y, x.Z, x.W));
                case Variant.Type.PackedColorArray: return Pack(v.AsColorArray(), x => (object)Arr(x.R, x.G, x.B, x.A));

                // Builtin structs: the generated codec emits the positional-array /
                // `_type`-table repr; returns null for anything genuinely unmappable.
                default: return GodotStructCodec.Decompose(v);
            }
        }

        // A resource property is either a saved asset (emit its `res://` path) or an
        // anonymous inline resource (emit `{ _type = "...", <non-default props> }`,
        // which GodotBinder.BuildResource reconstructs). Non-resource objects (e.g. a
        // node reference) are dropped — children are emitted structurally.
        private object? ResourceToToml(GodotObject? obj)
        {
            if (obj is not Resource res) return null;
            var path = res.ResourcePath;
            if (!string.IsNullOrEmpty(path) && path.StartsWith("res://")) return path;

            var cls = res.GetClass();
            var d = new Dictionary<string, object> { ["_type"] = cls };
            var def = Default(cls);
            if (def is not null)
                foreach (Godot.Collections.Dictionary p in res.GetPropertyList())
                {
                    var name = p["name"].AsString();
                    if (SkipResourceProp(name) || name.StartsWith("_")) continue;
                    var usage = (PropertyUsageFlags)p["usage"].AsInt64();
                    if ((usage & PropertyUsageFlags.Storage) == 0) continue;
                    if (!Differs(res.Get(name), def.Get(name))) continue;
                    var val = ToToml(res.Get(name));
                    if (val is not null) d[name] = val;
                }
            return d;
        }

        // Two property values differ iff their emitted forms differ (numeric-tolerant),
        // so float round-tripping noise and int-vs-float don't force spurious writes.
        private bool Differs(Variant cur, Variant def) => !ObjEquals(ToToml(cur), ToToml(def));

        public void Dispose()
        {
            foreach (var o in _defaults.Values)
                if (o is Node n) n.Free();   // Nodes are manually managed; Resources refcount away
            _defaults.Clear();
        }
    }

    // ---- SceneSpec -> TOML text ----------------------------------------------

    internal static string Emit(SceneSpec spec)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(spec.StartScene))
            sb.Append("start_scene = ").Append(Quote(spec.StartScene!)).Append('\n');
        if (!string.IsNullOrEmpty(spec.Controls))
            sb.Append("controls = ").Append(Quote(spec.Controls!)).Append('\n');
        if (!string.IsNullOrEmpty(spec.Scenario))
            sb.Append("scenario = ").Append(Quote(spec.Scenario!)).Append('\n');
        if (!string.IsNullOrEmpty(spec.Description))
            sb.Append("description = ").Append(Quote(spec.Description!)).Append('\n');
        if (spec.Player is { } player)
            sb.Append("player = { node = ").Append(Quote(player.Node))
              .Append(", spawn = ").Append(Quote(player.Spawn)).Append(" }\n");
        foreach (var n in spec.Nodes) EmitNode(sb, n, "nodes." + n.Name);
        return sb.ToString();
    }

    private static void EmitNode(StringBuilder sb, NodeSpec n, string path)
    {
        if (sb.Length > 0) sb.Append('\n');                 // blank line between blocks
        sb.Append('[').Append(path).Append("]\n");
        sb.Append("type = ").Append(Quote(n.Type)).Append('\n');
        if (n.Script is not null) sb.Append("script = ").Append(Quote(n.Script)).Append('\n');
        EmitAttachList(sb, "behaviors", n.Behaviors);
        EmitAttachList(sb, "machines", n.Machines);
        foreach (var kv in n.Props) sb.Append(EmitKey(kv.Key)).Append(" = ").Append(EmitValue(kv.Value)).Append('\n');
        if (n.Groups.Count > 0)
            sb.Append("groups = [").Append(string.Join(", ", n.Groups.Select(Quote))).Append("]\n");
        if (n.Params.Count > 0) sb.Append("params = ").Append(EmitInline(n.Params)).Append('\n');
        if (n.Meta.Count > 0) sb.Append("meta = ").Append(EmitInline(n.Meta)).Append('\n');
        if (n.Unique) sb.Append("unique = true\n");
        if (n.Connections.Count > 0)
            sb.Append("connections = [").Append(string.Join(", ", n.Connections.Select(c =>
                "{ signal = " + Quote(c.Signal) + ", to = " + Quote(c.To) + ", method = " + Quote(c.Method) + " }")))
              .Append("]\n");
        if (n.Instance is not null) sb.Append("instance = ").Append(Quote(n.Instance)).Append('\n');

        foreach (var c in n.Children) EmitNode(sb, c, path + "." + c.Name);
    }

    // `behaviors = [...]` / `machines = [...]`: a param-less entry emits as its bare
    // path string; one with params as `{ script = "...", params = {..} }`.
    private static void EmitAttachList(StringBuilder sb, string key, List<AttachSpec> list)
    {
        if (list.Count == 0) return;
        sb.Append(key).Append(" = [").Append(string.Join(", ", list.Select(a =>
            a.Params.Count == 0
                ? Quote(a.Script)
                : "{ script = " + Quote(a.Script) + ", params = " + EmitInline(a.Params) + " }")))
          .Append("]\n");
    }

    private static string EmitValue(object? v) => v switch
    {
        null => "\"\"",
        bool b => b ? "true" : "false",
        double d => FmtNum(d),
        string s => Quote(s),
        object[] arr => "[" + string.Join(", ", arr.Select(EmitValue)) + "]",
        Dictionary<string, object> m => EmitInline(m),
        _ => Quote(v.ToString() ?? ""),
    };

    private static string EmitInline(Dictionary<string, object> m)
        => "{ " + string.Join(", ", m.Select(kv => EmitKey(kv.Key) + " = " + EmitValue(kv.Value))) + " }";

    // A TOML key is bare if it is all ASCII [A-Za-z0-9_-]; otherwise quote it (so a
    // `/`-bearing property key like theme_override_colors/font_color is valid TOML).
    private static string EmitKey(string key)
    {
        foreach (var c in key)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
                return Quote(key);
        return key.Length == 0 ? Quote(key) : key;
    }

    // Whole-valued doubles emit as integers (3, not 3.0). Otherwise prefer the
    // shortest representation of the underlying float (Godot stores Vector/Color as
    // float32), so 0.55f doesn't widen to 0.550000011920929; fall back to the
    // shortest double for genuine float64s.
    private static string FmtNum(double d)
    {
        if (double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) < 1e15)
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        float f = (float)d;
        if ((double)f == d) return f.ToString("R", CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var ch in s)
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        return sb.Append('"').ToString();
    }

    // ---- helpers -------------------------------------------------------------

    // Transform-family properties on a spatial node: emitted (where non-default) as
    // the decomposed position/rotation/scale triple, so the monolithic/derived forms
    // are never written.
    private static readonly string[] TransformFamily =
    {
        "transform", "global_transform", "position", "rotation", "scale",
        "global_position", "global_rotation", "global_rotation_degrees", "rotation_degrees",
        "quaternion", "basis", "global_basis", "global_scale", "skew", "global_skew",
    };

    private static readonly HashSet<string> SkipAlways = new()
    {
        "name", "owner", "multiplayer", "script", "scene_file_path",
        "unique_name_in_owner",   // emitted structurally as `unique = true`
    };

    private static bool SkipResourceProp(string name) => name is
        "resource_local_to_scene" or "resource_name" or "resource_path" or "resource_scene_unique_id" or "script";

    private static object[] Arr(params double[] xs)
    {
        var o = new object[xs.Length];
        for (int i = 0; i < xs.Length; i++) o[i] = xs[i];
        return o;
    }

    private static object[] Pack<T>(T[] arr, Func<T, object> conv)
    {
        var o = new object[arr.Length];
        for (int i = 0; i < arr.Length; i++) o[i] = conv(arr[i]);
        return o;
    }

    private static bool Approx(Vector3 a, Vector3 b) => a.IsEqualApprox(b);
    private static bool Approx(Vector2 a, Vector2 b) => a.IsEqualApprox(b);
    private static bool Approx(float a, float b) => Mathf.IsEqualApprox(a, b);

    private static bool ObjEquals(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is double da && b is double db)
            return Math.Abs(da - db) <= 1e-6 * Math.Max(1.0, Math.Max(Math.Abs(da), Math.Abs(db)));
        if (a is bool ba && b is bool bb) return ba == bb;
        if (a is string sa && b is string sb) return sa == sb;
        if (a is object[] xa && b is object[] xb)
        {
            if (xa.Length != xb.Length) return false;
            for (int i = 0; i < xa.Length; i++) if (!ObjEquals(xa[i], xb[i])) return false;
            return true;
        }
        if (a is Dictionary<string, object> ma && b is Dictionary<string, object> mb)
        {
            if (ma.Count != mb.Count) return false;
            foreach (var kv in ma)
                if (!mb.TryGetValue(kv.Key, out var v) || !ObjEquals(kv.Value, v)) return false;
            return true;
        }
        return a.Equals(b);
    }

    private static readonly char[] ReservedNameChars = { '.', ':', '@', '/', '%', '"' };

    private static void GuardName(string name)
    {
        if (name.IndexOfAny(ReservedNameChars) >= 0)
            throw new EvaluateException(
                $"cannot serialize node name '{name}': it contains a reserved character " +
                "(any of . : @ / % \") that would not round-trip through a scene path");
    }
}
