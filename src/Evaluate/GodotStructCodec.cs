using System;
using System.Collections.Generic;
using System.Globalization;

namespace Evaluate;

// Mark the partial codec class so the generator fills it in. The generator
// enumerates Godot's builtin struct Variant types (Vector*, Color, Quaternion,
// Rect2*, Plane, Aabb, Basis, Transform2D/3D, Projection, …) from the GodotSharp
// reference and emits typed, **reflection-free** decompose/recompose code for each —
// so a new Godot builtin is covered automatically, with no hand edits and no runtime
// reflection (AOT/trim safe). See generator/StructCodecGenerator.cs.
[AttributeUsage(AttributeTargets.Class)]
public sealed class GodotStructCodecAttribute : Attribute { }

// The codec works on Evaluate's neutral TOML object model (double / string / bool /
// object[] / Dictionary<string,object>) — the SAME shape SceneFile parses and
// SceneWriter emits — so both the serializer and the binder share one source of truth.
// An all-scalar struct (a vector/color) is a positional array `[a, b, …]`; a composite
// struct is a `_type`-tagged table `{ _type = "Name", comp = …, … }`.
[GodotStructCodec]
internal static partial class GodotStructCodec
{
    // ---- repr helpers used by the generated code ----------------------------

    internal static double Num(object? o) => o switch
    {
        double d => d,
        long l => l,
        int i => i,
        float f => f,
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0,
        _ => 0,
    };

    // The i-th scalar of a positional list (or 0 if absent / not a number).
    internal static double At(object[] a, int i) => i >= 0 && i < a.Length ? Num(a[i]) : 0;

    internal static object[] AsArr(object? o) => o as object[] ?? Array.Empty<object>();

    // A component value addressed by name (composite `_type` table) OR by index
    // (positional array, in the struct's field order) — so both the canonical
    // written form and a hand-authored positional form recompose.
    internal static object Field(object repr, string name, int index) =>
        repr is Dictionary<string, object> d && d.TryGetValue(name, out var v) ? v
        : repr is object[] a && index < a.Length ? a[index]
        : repr;

    internal static Dictionary<string, object> Table(string type, params (string k, object v)[] fields)
    {
        var d = new Dictionary<string, object>(fields.Length + 1) { ["_type"] = type };
        foreach (var (k, v) in fields) d[k] = v;
        return d;
    }

    internal static object[] Scalars(params double[] xs)
    {
        var o = new object[xs.Length];
        for (int i = 0; i < xs.Length; i++) o[i] = xs[i];
        return o;
    }
}
