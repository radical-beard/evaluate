using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using Lua;

namespace Evaluate;

// Capability story note: per project decision, Godot types are AMBIENT (default,
// not opt-in). A script reaches any type via `godot.<Type>`; the binder resolves
// it lazily by reflection over GodotSharp. (config/entity/world/input remain
// declared capabilities.)
//
// Member access on an instance routes through the engine ClassDB
// (GodotObject.Call/Get/Set, snake_case names) rather than System.Reflection —
// this reaches engine/GDScript/GDExtension members uniformly via Variant.
public sealed class GodotBinder
{
    private static readonly Assembly GodotAssembly = typeof(GodotObject).Assembly;

    private readonly LuaState _lua;
    private readonly Dictionary<string, Dictionary<string, Variant.Type>> _propTypes = new();
    public GodotBinder(LuaState lua) => _lua = lua;

    // godot.<TypeName> -> a "type handle": `new` constructor + Lua-convertible statics.
    public LuaValue? Resolve(string typeName)
    {
        var type = GodotAssembly.GetType($"Godot.{typeName}");
        if (type is null) return null;

        // Enums (godot.Key.Space, godot.MouseButton.Left, …) -> a table of values.
        if (type.IsEnum)
        {
            var et = new LuaTable();
            foreach (var name in Enum.GetNames(type))
                et[name] = Convert.ToDouble(Convert.ChangeType(Enum.Parse(type, name), typeof(long)));
            return et;
        }

        // Prefer a pre-baked (source-generated, zero-reflection) binding if present.
        var prebaked = Prebaked.TryGet(typeName, out var handle);
        if (!prebaked) handle = new LuaTable();

        if (typeof(GodotObject).IsAssignableFrom(type) && !type.IsAbstract)
        {
            handle["new"] = new LuaFunction((ctx, ct) =>
            {
                var obj = (GodotObject)Activator.CreateInstance(type)!;
                ctx.Return(WrapInstance(obj));
                return new(1);
            });
        }

        // Lazily resolve nested enums (godot.Timer.TimerProcessCallback) and
        // public static constants on access.
        var mt = new LuaTable();
        mt["__index"] = new LuaFunction((ctx, ct) =>
        {
            var key = ctx.GetArgument(1).Read<string>();
            var nested = GodotAssembly.GetType($"Godot.{typeName}+{key}");
            if (nested is { IsEnum: true })
            {
                var et = new LuaTable();
                foreach (var nm in Enum.GetNames(nested))
                    et[nm] = Convert.ToDouble(Convert.ChangeType(Enum.Parse(nested, nm), typeof(long)));
                handle![key] = et;                  // memoize
                ctx.Return(et);
                return new(1);
            }
            var field = type.GetField(key, BindingFlags.Public | BindingFlags.Static);
            if (field is not null && (field.IsLiteral || field.IsInitOnly))
            {
                ctx.Return(FromClr(field.GetValue(null)));
                return new(1);
            }
            ctx.Return(LuaValue.Nil);
            return new(1);
        });
        handle!.Metatable = mt;

        // Bind public static methods by reflection. We marshal the full Variant
        // type set (structs, packed arrays, objects, collections), so any method
        // whose whole signature we can marshal is bound. Names already provided by
        // a pre-baked binding are kept (the generator's zero-reflection versions win).
        foreach (var group in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                   .Where(m => !m.IsSpecialName && !m.IsGenericMethod)
                                   .Where(m => m.GetParameters().All(p => CanMarshal(p.ParameterType))
                                            && (m.ReturnType == typeof(void) || CanMarshal(m.ReturnType)))
                                   .GroupBy(m => m.Name))
        {
            if (handle[group.Key].Type != LuaValueType.Nil) continue;   // pre-baked wins
            var overloads = group.ToArray();
            handle[group.Key] = new LuaFunction((ctx, ct) =>
            {
                var n = ctx.ArgumentCount;
                // Pick an overload whose required..total param count brackets the arg count.
                var method = overloads.FirstOrDefault(m =>
                {
                    var p = m.GetParameters();
                    return n >= p.Count(x => !x.IsOptional) && n <= p.Length;
                }) ?? overloads[0];

                var ps = method.GetParameters();
                var args = new object?[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                    args[i] = i < n ? ToClr(ctx.GetArgument(i), ps[i].ParameterType)
                                    : ps[i].HasDefaultValue ? ps[i].DefaultValue
                                    : ToClr(LuaValue.Nil, ps[i].ParameterType);
                ctx.Return(FromClr(method.Invoke(null, args)));
                return new(1);
            });
        }
        return handle;
    }

    // A live Godot object exposed to Lua as userdata (so it can pass back into
    // the engine — e.g. world:add_child(node)). Its metatable routes every
    // access into the engine: `obj:method(a,b)`, `obj.prop`, `obj.prop = v`,
    // and `obj:connect("signal", fn)`.
    public LuaValue WrapInstance(GodotObject obj)
    {
        var mt = new LuaTable();

        mt["__index"] = new LuaFunction((ctx, ct) =>
        {
            var key = ctx.GetArgument(1).Read<string>();
            if (key == "connect") { ctx.Return(MakeConnect(obj)); return new(1); }
            if (obj.HasMethod(key)) { ctx.Return(MakeMethod(obj, key)); return new(1); }
            ctx.Return(ToLua(obj.Get(key)));
            return new(1);
        });
        mt["__newindex"] = new LuaFunction((ctx, ct) =>
        {
            var key = ctx.GetArgument(1).Read<string>();
            obj.Set(key, ToSetValue(obj, key, ctx.GetArgument(2)));
            return new(0);
        });

        return new LuaValue(new GodotInstanceProxy(obj) { Metatable = mt });
    }

    // Construct a Godot object by type name (e.g. "Node3D") — the C#-side
    // equivalent of the `new` exposed on a `godot.<Type>` handle. Used to build
    // nodes declared in scene files. Null if the name isn't a constructible type.
    public GodotObject? Instantiate(string typeName)
    {
        var type = GodotAssembly.GetType($"Godot.{typeName}");
        if (type is null || !typeof(GodotObject).IsAssignableFrom(type) || type.IsAbstract) return null;
        return (GodotObject)Activator.CreateInstance(type)!;
    }

    // Set an engine property from a Lua value, with the same target-type-aware
    // marshalling as `node.prop = v` (so scene-file `position = [0,1,2]` becomes
    // a Vector3). Routes through the engine, not reflection.
    public void SetProperty(GodotObject obj, string key, LuaValue val) =>
        obj.Set(key, ToSetValue(obj, key, val));

    private LuaValue MakeMethod(GodotObject obj, string name) => new LuaFunction((ctx, ct) =>
    {
        var n = ctx.ArgumentCount - 1;                 // arg 0 is self (colon call)
        var gargs = n > 0 ? new Variant[n] : Array.Empty<Variant>();
        for (int i = 0; i < n; i++) gargs[i] = ToVariant(ctx.GetArgument(i + 1));
        ctx.Return(ToLua(obj.Call(name, gargs)));
        return new(1);
    });

    private LuaValue MakeConnect(GodotObject obj) => new LuaFunction((ctx, ct) =>
    {
        var signal = ctx.GetArgument(1).Read<string>();
        var fn = ctx.GetArgument(2);
        var err = obj.Connect(signal, BuildCallable(signal, fn, SignalArgCount(obj, signal)));
        ctx.Return((double)(long)err);
        return new(1);
    });

    private Callable BuildCallable(string signal, LuaValue fn, int argc) => argc switch
    {
        0 => Callable.From(() => InvokeLua(fn)),
        1 => Callable.From<Variant>(a => InvokeLua(fn, a)),
        2 => Callable.From<Variant, Variant>((a, b) => InvokeLua(fn, a, b)),
        3 => Callable.From<Variant, Variant, Variant>((a, b, c) => InvokeLua(fn, a, b, c)),
        4 => Callable.From<Variant, Variant, Variant, Variant>((a, b, c, d) => InvokeLua(fn, a, b, c, d)),
        5 => Callable.From<Variant, Variant, Variant, Variant, Variant>((a, b, c, d, e) => InvokeLua(fn, a, b, c, d, e)),
        6 => Callable.From<Variant, Variant, Variant, Variant, Variant, Variant>((a, b, c, d, e, f) => InvokeLua(fn, a, b, c, d, e, f)),
        _ => throw new EvaluateException($"signal '{signal}' has {argc} args (>6 not yet supported)"),
    };

    private void InvokeLua(LuaValue fn, params Variant[] gargs)
    {
        var args = new LuaValue[gargs.Length];
        for (int i = 0; i < gargs.Length; i++) args[i] = ToLua(gargs[i]);
        var vt = _lua.CallAsync(fn, args);             // main thread; sync bridge
        if (!vt.IsCompleted) vt.AsTask().GetAwaiter().GetResult();
    }

    private static int SignalArgCount(GodotObject obj, string signal)
    {
        foreach (Godot.Collections.Dictionary entry in obj.GetSignalList())
            if ((string)entry["name"] == signal)
                return ((Godot.Collections.Array)entry["args"]).Count;
        return 0;
    }

    // ---- marshalling ----------------------------------------------------------

    private LuaValue ToLua(Variant v) => v.VariantType switch
    {
        Variant.Type.Nil => LuaValue.Nil,
        Variant.Type.Bool => v.AsBool(),
        Variant.Type.Int => (double)v.AsInt64(),
        Variant.Type.Float => v.AsDouble(),
        Variant.Type.String or Variant.Type.StringName or Variant.Type.NodePath => v.AsString(),
        Variant.Type.Object => WrapInstance(v.AsGodotObject()),
        Variant.Type.Array => ArrayToTable(v.AsGodotArray()),
        Variant.Type.Dictionary => DictToTable(v.AsGodotDictionary()),

        // Rich C#-backed value types for the hot, ergonomic cases (have methods).
        Variant.Type.Vector2 => new EvaluateVec2(v.AsVector2().X, v.AsVector2().Y),
        Variant.Type.Vector3 => new EvaluateVec3(v.AsVector3().X, v.AsVector3().Y, v.AsVector3().Z),
        Variant.Type.Color => MakeColor(v.AsColor()),

        // Every other builtin struct (Vector4, Quaternion, Rect2, Aabb, Basis,
        // Transform2D/3D, Projection, …) goes through the generated codec — `_ =>` below.

        // Packed arrays -> Lua list tables.
        Variant.Type.PackedByteArray => Pack(v.AsByteArray(), b => (double)b),
        Variant.Type.PackedInt32Array => Pack(v.AsInt32Array(), n => (double)n),
        Variant.Type.PackedInt64Array => Pack(v.AsInt64Array(), n => (double)n),
        Variant.Type.PackedFloat32Array => Pack(v.AsFloat32Array(), f => (double)f),
        Variant.Type.PackedFloat64Array => Pack(v.AsFloat64Array(), d => d),
        Variant.Type.PackedStringArray => Pack(v.AsStringArray(), s => (LuaValue)s),
        Variant.Type.PackedVector2Array => Pack(v.AsVector2Array(), V2T),
        Variant.Type.PackedVector3Array => Pack(v.AsVector3Array(), V3T),
        Variant.Type.PackedVector4Array => Pack(v.AsVector4Array(), V4T),
        Variant.Type.PackedColorArray => Pack(v.AsColorArray(), c => MakeColor(c)),

        _ => StructOrString(v),                         // builtin struct via codec, else string
    };

    // A builtin struct -> its neutral repr as a Lua value (the codec's positional array /
    // `_type` table, marshalled to a Lua table). Non-structs fall back to a string.
    private static LuaValue StructOrString(Variant v) =>
        GodotStructCodec.Decompose(v) is { } o ? SceneBuilder.TomlToLua(o) : v.ToString();

    // ---- struct <-> Lua-table helpers ----------------------------------------

    private static LuaValue Tbl(params (string k, LuaValue v)[] fields)
    {
        var t = new LuaTable();
        foreach (var (k, val) in fields) t[k] = val;
        return t;
    }

    private static LuaValue V2T(Vector2 v) => Tbl(("x", (double)v.X), ("y", (double)v.Y));
    private static LuaValue V3T(Vector3 v) => Tbl(("x", (double)v.X), ("y", (double)v.Y), ("z", (double)v.Z));
    private static LuaValue V4T(Vector4 v) => Tbl(("x", (double)v.X), ("y", (double)v.Y), ("z", (double)v.Z), ("w", (double)v.W));
    private static LuaValue QuatT(Quaternion q) => Tbl(("x", (double)q.X), ("y", (double)q.Y), ("z", (double)q.Z), ("w", (double)q.W));
    private static LuaValue BasisT(Basis b) => Tbl(("x", V3T(b.X)), ("y", V3T(b.Y)), ("z", V3T(b.Z)));

    private static LuaValue Pack<T>(T[] arr, Func<T, LuaValue> conv)
    {
        var t = new LuaTable();
        for (int i = 0; i < arr.Length; i++) t[i + 1] = conv(arr[i]);
        return t;
    }

    // Target-aware property write: if the destination is a struct and the Lua
    // value is a plain table, build the exact struct from its named fields. This
    // makes the read-modify-write pattern (node.transform = t) work for every
    // struct, without a dedicated std type per struct. A positional list
    // ([x,y,z]) targeting a vector/color is also accepted, so scene files can
    // write `position = [0, 1, 2]`.
    private Variant ToSetValue(GodotObject obj, string key, LuaValue val)
    {
        if (val.Type == LuaValueType.Table)
        {
            var t = val.Read<LuaTable>();
            // `_type` names either a builtin struct (`{ _type = "AABB", position = [..], ... }`)
            // or a Resource (`{ _type = "BoxMesh", size = [..] }`): a struct builds by value,
            // a resource instantiates. This lets struct-valued properties live in a `.scene`,
            // which otherwise can't carry them (a bare `{..}` table parses as a child node).
            if (t["_type"].Type == LuaValueType.String)
            {
                var typeName = t["_type"].Read<string>();
                return GodotStructCodec.TypeByName(typeName) is { } st
                    ? GodotStructCodec.Recompose(st, TableToObjModel(t))   // builtin struct
                    : BuildResource(t);                                    // Resource
            }

            var pt = PropertyType(obj, key);
            if (pt is { } vt && GodotStructCodec.IsStruct(vt))
                return GodotStructCodec.Recompose(vt, TableToObjModel(t));
        }
        // Resource-by-path: a res:// string targeting a Resource (Object) property -> load it,
        // so a scene file can write `texture = "res://art/x.png"` / `mesh = "res://m.tres"`.
        else if (val.Type == LuaValueType.String)
        {
            var s = val.Read<string>();
            if (s.StartsWith("res://") && PropertyType(obj, key) == Variant.Type.Object
                && ResourceLoader.Load(s) is { } res)
                return res;
        }
        return ToVariant(val);
    }

    // Construct a Godot Resource from an inline `{ _type = "...", <prop> = <value>, ... }`
    // table; props are set recursively, so nested sub-resources / res:// paths work.
    private Variant BuildResource(LuaTable t)
    {
        var typeName = t["_type"].Read<string>();
        var res = Instantiate(typeName)
            ?? throw new EvaluateException($"inline resource has unknown _type '{typeName}'");
        foreach (var kv in t)
        {
            if (kv.Key.Type != LuaValueType.String) continue;
            var k = kv.Key.Read<string>();
            if (k != "_type") SetProperty(res, k, kv.Value);
        }
        return res;
    }

    private Variant.Type? PropertyType(GodotObject obj, string key)
    {
        var cls = obj.GetClass();
        if (!_propTypes.TryGetValue(cls, out var map))
        {
            map = new();
            foreach (Godot.Collections.Dictionary p in obj.GetPropertyList())
                map[p["name"].AsString()] = (Variant.Type)p["type"].AsInt64();
            _propTypes[cls] = map;
        }
        return map.TryGetValue(key, out var t) ? t : null;
    }

    // Build a Variant struct from a Lua table (named `{x=…}`, positional `[x,…]`, or a
    // `_type` table) via the generated, reflection-free codec. Kept for the static-method
    // marshalling path (StructFromLua); property writes call GodotStructCodec directly.
    private static Variant TableToStruct(LuaTable t, Variant.Type vt) =>
        GodotStructCodec.Recompose(vt, TableToObjModel(t));

    // Lua value -> Evaluate's neutral object model (double / string / bool / object[] /
    // Dictionary<string,object>) the struct codec consumes. Cold path (struct props only).
    internal static object? LuaToObjModel(LuaValue v) => v.Type switch
    {
        LuaValueType.Boolean => v.Read<bool>(),
        LuaValueType.Number => v.Read<double>(),
        LuaValueType.String => v.Read<string>(),
        LuaValueType.Table => TableToObjModel(v.Read<LuaTable>()),
        _ => null,
    };

    private static object TableToObjModel(LuaTable t)
    {
        if (t.ArrayLength > 0 && t.HashMapCount == 0)
        {
            var arr = new object[t.ArrayLength];
            for (int i = 0; i < arr.Length; i++) arr[i] = LuaToObjModel(t[i + 1])!;
            return arr;
        }
        var map = new Dictionary<string, object>();
        foreach (var kv in t)
            if (kv.Key.Type == LuaValueType.String) map[kv.Key.Read<string>()] = LuaToObjModel(kv.Value)!;
        return map;
    }

    private static float F(LuaTable t, string k) => t[k].Type == LuaValueType.Number ? (float)t[k].Read<double>() : 0f;
    private static float P(LuaTable t, int i) => t[i].Type == LuaValueType.Number ? (float)t[i].Read<double>() : 0f;
    private static bool IsList(LuaTable t, int n) => t.ArrayLength >= n && t.HashMapCount == 0;

    // Read a Vector2/3 from a std userdata, a named `{x,y[,z]}` table, or a positional list
    // (used by the packed-array element marshalling).
    private static Vector2 Vec2(LuaValue v)
    {
        if (v.TryRead<EvaluateVec2>(out var e2)) return new Vector2((float)e2.X, (float)e2.Y);
        if (v.TryRead<EvaluateVec3>(out var e3)) return new Vector2((float)e3.X, (float)e3.Y);
        if (v.Type == LuaValueType.Table) { var t = v.Read<LuaTable>(); return IsList(t, 2) ? new Vector2(P(t, 1), P(t, 2)) : new Vector2(F(t, "x"), F(t, "y")); }
        return default;
    }

    private static Vector3 Vec3(LuaValue v)
    {
        if (v.TryRead<EvaluateVec3>(out var e3)) return new Vector3((float)e3.X, (float)e3.Y, (float)e3.Z);
        if (v.Type == LuaValueType.Table) { var t = v.Read<LuaTable>(); return IsList(t, 3) ? new Vector3(P(t, 1), P(t, 2), P(t, 3)) : new Vector3(F(t, "x"), F(t, "y"), F(t, "z")); }
        return default;
    }

    private Variant ToVariant(LuaValue v)
    {
        switch (v.Type)
        {
            case LuaValueType.Boolean: return v.Read<bool>();
            case LuaValueType.Number: return v.Read<double>();
            case LuaValueType.String: return v.Read<string>();
            case LuaValueType.Table: return TableToVariant(v.Read<LuaTable>());
            case LuaValueType.UserData:
                // A Godot instance proxy marshals back to its underlying object.
                if (v.TryRead<GodotInstanceProxy>(out var p)) return p.Target;
                // std.* value types marshal to their Godot struct equivalents.
                if (v.TryRead<EvaluateVec3>(out var e3)) return new Vector3((float)e3.X, (float)e3.Y, (float)e3.Z);
                if (v.TryRead<EvaluateVec2>(out var e2)) return new Vector2((float)e2.X, (float)e2.Y);
                if (v.TryRead<EvaluateColor>(out var ec)) return new Color((float)ec.R, (float)ec.G, (float)ec.B, (float)ec.A);
                return new Variant();
            default: return new Variant();
        }
    }

    private static LuaValue MakeColor(Color c) => new EvaluateColor(c.R, c.G, c.B, c.A);

    // A Lua table marshals to a Godot Array if it's a 1..n sequence, else a Dictionary.
    private Variant TableToVariant(LuaTable t)
    {
        var n = t.ArrayLength;
        var isSequence = n > 0 && t.HashMapCount == 0;
        if (isSequence)
        {
            var arr = new Godot.Collections.Array();
            for (int i = 1; i <= n; i++) arr.Add(ToVariant(t[i]));
            return arr;
        }
        var dict = new Godot.Collections.Dictionary();
        foreach (var kv in t) dict[ToVariant(kv.Key)] = ToVariant(kv.Value);
        return dict;
    }

    private LuaValue ArrayToTable(Godot.Collections.Array a)
    {
        var t = new LuaTable();
        for (int i = 0; i < a.Count; i++) t[i + 1] = ToLua(a[i]);   // 1-based for Lua
        return t;
    }

    private LuaValue DictToTable(Godot.Collections.Dictionary d)
    {
        var t = new LuaTable();
        foreach (var k in d.Keys) t[ToLua(k)] = ToLua(d[k]);
        return t;
    }

    // ---- static-method marshalling (reflection path) -------------------------

    private static bool IsNumeric(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(uint) || t == typeof(ulong)
        || t == typeof(short) || t == typeof(ushort) || t == typeof(byte) || t == typeof(sbyte)
        || t == typeof(float) || t == typeof(double);

    // Any C# type we can convert to/from a Lua value (the full Variant set).
    private static bool CanMarshal(Type t)
    {
        if (t.IsByRef || t.IsPointer || t.IsGenericParameter) return false;
        if (IsNumeric(t) || t == typeof(bool) || t == typeof(string) || t.IsEnum) return true;
        if (t == typeof(Variant) || t == typeof(StringName) || t == typeof(NodePath)) return true;
        if (typeof(GodotObject).IsAssignableFrom(t)) return true;
        if (t == typeof(Godot.Collections.Array) || t == typeof(Godot.Collections.Dictionary)) return true;
        return StructType(t) is not null || PackedElem(t) is not null;
    }

    private static Variant.Type? StructType(Type t) =>
        t == typeof(Vector2) ? Variant.Type.Vector2 :
        t == typeof(Vector3) ? Variant.Type.Vector3 :
        t == typeof(Vector4) ? Variant.Type.Vector4 :
        t == typeof(Vector2I) ? Variant.Type.Vector2I :
        t == typeof(Vector3I) ? Variant.Type.Vector3I :
        t == typeof(Color) ? Variant.Type.Color :
        t == typeof(Quaternion) ? Variant.Type.Quaternion :
        t == typeof(Rect2) ? Variant.Type.Rect2 :
        t == typeof(Plane) ? Variant.Type.Plane :
        t == typeof(Aabb) ? Variant.Type.Aabb :
        t == typeof(Basis) ? Variant.Type.Basis :
        t == typeof(Transform2D) ? Variant.Type.Transform2D :
        t == typeof(Transform3D) ? Variant.Type.Transform3D :
        (Variant.Type?)null;

    // C# element type for a packed-array param/return (PackedInt32Array -> int[]).
    private static Type? PackedElem(Type t) =>
        t == typeof(byte[]) || t == typeof(int[]) || t == typeof(long[]) || t == typeof(float[])
        || t == typeof(double[]) || t == typeof(string[]) || t == typeof(Vector2[])
        || t == typeof(Vector3[]) || t == typeof(Color[]) ? t.GetElementType() : null;

    private object? ToClr(LuaValue v, Type target)
    {
        if (target == typeof(string)) return v.ToString();
        if (target == typeof(bool)) return v.Read<bool>();
        if (target.IsEnum) return Enum.ToObject(target, (long)v.Read<double>());
        if (IsNumeric(target)) return Convert.ChangeType(v.Read<double>(), target);
        if (target == typeof(StringName)) return new StringName(v.ToString());
        if (target == typeof(NodePath)) return new NodePath(v.ToString());
        if (target == typeof(Variant)) return ToVariant(v);
        if (typeof(GodotObject).IsAssignableFrom(target))
            return v.TryRead<GodotInstanceProxy>(out var p) ? p.Target : null;
        if (target == typeof(Godot.Collections.Array)) return ToVariant(v).AsGodotArray();
        if (target == typeof(Godot.Collections.Dictionary)) return ToVariant(v).AsGodotDictionary();
        if (StructType(target) is { } vt) return StructFromLua(v, vt);
        if (PackedElem(target) is { } elem) return PackedFromLua(v, elem);
        return null;
    }

    // Build a boxed Godot struct from a Lua std userdata or named-field table.
    private object StructFromLua(LuaValue v, Variant.Type vt)
    {
        if (vt == Variant.Type.Vector3 && v.TryRead<EvaluateVec3>(out var e3)) return new Vector3((float)e3.X, (float)e3.Y, (float)e3.Z);
        if (vt == Variant.Type.Vector2 && v.TryRead<EvaluateVec2>(out var e2)) return new Vector2((float)e2.X, (float)e2.Y);
        if (vt == Variant.Type.Color && v.TryRead<EvaluateColor>(out var ec)) return new Color((float)ec.R, (float)ec.G, (float)ec.B, (float)ec.A);
        var t = v.Type == LuaValueType.Table ? v.Read<LuaTable>() : new LuaTable();
        var variant = TableToStruct(t, vt);    // builds the struct as a Variant
        return vt switch                       // unbox to the concrete struct for Invoke
        {
            Variant.Type.Vector2 => variant.AsVector2(),
            Variant.Type.Vector3 => variant.AsVector3(),
            Variant.Type.Vector4 => variant.AsVector4(),
            Variant.Type.Vector2I => variant.AsVector2I(),
            Variant.Type.Vector3I => variant.AsVector3I(),
            Variant.Type.Color => variant.AsColor(),
            Variant.Type.Quaternion => variant.AsQuaternion(),
            Variant.Type.Rect2 => variant.AsRect2(),
            Variant.Type.Plane => variant.AsPlane(),
            Variant.Type.Aabb => variant.AsAabb(),
            Variant.Type.Basis => variant.AsBasis(),
            Variant.Type.Transform2D => variant.AsTransform2D(),
            Variant.Type.Transform3D => variant.AsTransform3D(),
            _ => new Vector3(),
        };
    }

    private object PackedFromLua(LuaValue v, Type elem)
    {
        var t = v.Type == LuaValueType.Table ? v.Read<LuaTable>() : new LuaTable();
        int n = t.ArrayLength;
        if (elem == typeof(byte)) { var a = new byte[n]; for (int i = 0; i < n; i++) a[i] = (byte)t[i + 1].Read<double>(); return a; }
        if (elem == typeof(int)) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = (int)t[i + 1].Read<double>(); return a; }
        if (elem == typeof(long)) { var a = new long[n]; for (int i = 0; i < n; i++) a[i] = (long)t[i + 1].Read<double>(); return a; }
        if (elem == typeof(float)) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = (float)t[i + 1].Read<double>(); return a; }
        if (elem == typeof(double)) { var a = new double[n]; for (int i = 0; i < n; i++) a[i] = t[i + 1].Read<double>(); return a; }
        if (elem == typeof(string)) { var a = new string[n]; for (int i = 0; i < n; i++) a[i] = t[i + 1].ToString(); return a; }
        if (elem == typeof(Vector2)) { var a = new Vector2[n]; for (int i = 0; i < n; i++) a[i] = Vec2(t[i + 1]); return a; }
        if (elem == typeof(Vector3)) { var a = new Vector3[n]; for (int i = 0; i < n; i++) a[i] = Vec3(t[i + 1]); return a; }
        if (elem == typeof(Color)) { var a = new Color[n]; for (int i = 0; i < n; i++) a[i] = (Color)StructFromLua(t[i + 1], Variant.Type.Color); return a; }
        return Array.CreateInstance(elem, 0);
    }

    private LuaValue FromClr(object? o) => o switch
    {
        null => LuaValue.Nil,
        bool b => b,
        string s => s,
        double d => d,
        float f => (double)f,
        int i => (double)i,
        long l => (double)l,
        uint u => (double)u,
        ulong ul => (double)ul,
        short sh => (double)sh,
        ushort us => (double)us,
        byte bt => (double)bt,
        sbyte sb => (double)sb,
        Vector2 v => new EvaluateVec2(v.X, v.Y),
        Vector3 v => new EvaluateVec3(v.X, v.Y, v.Z),
        Color c => MakeColor(c),
        Vector4 v => V4T(v),
        Vector2I v => Tbl(("x", (double)v.X), ("y", (double)v.Y)),
        Vector3I v => Tbl(("x", (double)v.X), ("y", (double)v.Y), ("z", (double)v.Z)),
        Quaternion q => QuatT(q),
        Rect2 r => Tbl(("position", V2T(r.Position)), ("size", V2T(r.Size))),
        Plane p => Tbl(("normal", V3T(p.Normal)), ("d", (double)p.D)),
        Aabb a => Tbl(("position", V3T(a.Position)), ("size", V3T(a.Size))),
        Basis b => BasisT(b),
        Transform2D t2 => Tbl(("x", V2T(t2.X)), ("y", V2T(t2.Y)), ("origin", V2T(t2.Origin))),
        Transform3D t3 => Tbl(("basis", BasisT(t3.Basis)), ("origin", V3T(t3.Origin))),
        StringName sn => sn.ToString(),
        NodePath np => np.ToString(),
        Variant var => ToLua(var),
        Godot.Collections.Array arr => ArrayToTable(arr),
        Godot.Collections.Dictionary dict => DictToTable(dict),
        GodotObject go => WrapInstance(go),
        byte[] arr => Pack(arr, x => (double)x),
        int[] arr => Pack(arr, x => (double)x),
        long[] arr => Pack(arr, x => (double)x),
        float[] arr => Pack(arr, x => (double)x),
        double[] arr => Pack(arr, x => x),
        string[] arr => Pack(arr, x => (LuaValue)x),
        Vector2[] arr => Pack(arr, V2T),
        Vector3[] arr => Pack(arr, V3T),
        Color[] arr => Pack(arr, c => MakeColor(c)),
        _ => o.ToString() ?? "",
    };
}

// A live Godot object handed to Lua as userdata. Carries the GodotObject so it
// can be marshalled back into the engine, and a metatable that routes access.
public sealed class GodotInstanceProxy : ILuaUserData
{
    public LuaTable? Metatable { get; set; }
    public Godot.GodotObject Target { get; }
    public GodotInstanceProxy(Godot.GodotObject target) => Target = target;
}
