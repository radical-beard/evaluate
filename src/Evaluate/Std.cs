using System;
using System.Collections.Generic;
using Lua;

namespace Evaluate;

// std.* data types. These are real C#-backed objects exposed to Lua as
// userdata (via the [LuaObject] source generator) — NOT Lua tables. Interop is
// generated at compile time, so there's no runtime reflection.

[LuaObject]
public partial class EvaluateVec3
{
    [LuaMember("x")] public double X { get; set; }
    [LuaMember("y")] public double Y { get; set; }
    [LuaMember("z")] public double Z { get; set; }

    public EvaluateVec3() { }
    public EvaluateVec3(double x, double y, double z) { X = x; Y = y; Z = z; }

    [LuaMember("length")] public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);
    [LuaMember("copy")] public EvaluateVec3 Copy() => new(X, Y, Z);

    public override string ToString() => $"({X}, {Y}, {Z})";
}

[LuaObject]
public partial class EvaluateVec2
{
    [LuaMember("x")] public double X { get; set; }
    [LuaMember("y")] public double Y { get; set; }

    public EvaluateVec2() { }
    public EvaluateVec2(double x, double y) { X = x; Y = y; }

    [LuaMember("length")] public double Length() => Math.Sqrt(X * X + Y * Y);
    [LuaMember("copy")] public EvaluateVec2 Copy() => new(X, Y);
}

[LuaObject]
public partial class EvaluateColor
{
    [LuaMember("r")] public double R { get; set; }
    [LuaMember("g")] public double G { get; set; }
    [LuaMember("b")] public double B { get; set; }
    [LuaMember("a")] public double A { get; set; }

    public EvaluateColor() { A = 1; }
    public EvaluateColor(double r, double g, double b, double a) { R = r; G = g; B = b; A = a; }
}

// C++-style growable array, 1-indexed for Lua ergonomics.
[LuaObject]
public partial class EvaluateVector
{
    private readonly List<LuaValue> _items = new();

    [LuaMember("push_back")] public void PushBack(LuaValue v) => _items.Add(v);
    [LuaMember("pop_back")] public void PopBack() { if (_items.Count > 0) _items.RemoveAt(_items.Count - 1); }
    [LuaMember("get")] public LuaValue Get(int i) => _items[i - 1];
    [LuaMember("set")] public void Set(int i, LuaValue v) => _items[i - 1] = v;
    [LuaMember("size")] public int Size() => _items.Count;
    [LuaMember("clear")] public void Clear() => _items.Clear();
}

[LuaObject]
public partial class EvaluateLinkedList
{
    private readonly LinkedList<LuaValue> _list = new();

    [LuaMember("push_front")] public void PushFront(LuaValue v) => _list.AddFirst(v);
    [LuaMember("push_back")] public void PushBack(LuaValue v) => _list.AddLast(v);
    [LuaMember("pop_front")] public void PopFront() { if (_list.First is { } n) _list.Remove(n); }
    [LuaMember("pop_back")] public void PopBack() { if (_list.Last is { } n) _list.Remove(n); }
    [LuaMember("front")] public LuaValue Front() => _list.First!.Value;
    [LuaMember("back")] public LuaValue Back() => _list.Last!.Value;
    [LuaMember("size")] public int Size() => _list.Count;
}

// Builds the always-available `std` table: pure data types, no engine/IO reach,
// so it is NOT capability-gated (unlike the engine APIs).
public static class Std
{
    public static LuaTable Build()
    {
        var std = new LuaTable();

        std["vec3"] = new LuaFunction((ctx, ct) =>
        {
            ctx.Return(MakeVec3(ctx));
            return new(1);
        });
        std["vec2"] = new LuaFunction((ctx, ct) =>
        {
            ctx.Return(new EvaluateVec2(ctx.GetArgument<double>(0), ctx.GetArgument<double>(1)));
            return new(1);
        });
        std["color"] = new LuaFunction((ctx, ct) =>
        {
            ctx.Return(new EvaluateColor(
                ctx.GetArgument<double>(0), ctx.GetArgument<double>(1),
                ctx.GetArgument<double>(2), ctx.ArgumentCount > 3 ? ctx.GetArgument<double>(3) : 1.0));
            return new(1);
        });
        std["vector"] = new LuaFunction((ctx, ct) =>
        {
            ctx.Return(new EvaluateVector());
            return new(1);
        });
        std["linked_list"] = new LuaFunction((ctx, ct) =>
        {
            ctx.Return(new EvaluateLinkedList());
            return new(1);
        });

        return std;
    }

    // std.vec3(x, y, z)  or  std.vec3({a, b, c})  (e.g. a toml array)
    private static EvaluateVec3 MakeVec3(LuaFunctionExecutionContext ctx)
    {
        var a0 = ctx.GetArgument(0);
        if (a0.Type == LuaValueType.Table)
        {
            var t = a0.Read<LuaTable>();
            return new EvaluateVec3(t[1].Read<double>(), t[2].Read<double>(), t[3].Read<double>());
        }
        return new EvaluateVec3(
            ctx.GetArgument<double>(0),
            ctx.GetArgument<double>(1),
            ctx.GetArgument<double>(2));
    }
}
