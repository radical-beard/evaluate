using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Lua;

namespace Evaluate;

// Generates the full Lua API spec for whatever Godot + Evaluate a consumer has
// installed — the runtime analogue of the build-time codegen in Evaluate.Generator.
//
// Nothing here is a hand-written or hardcoded API list (the only static bit is the
// frontmatter *grammar*, which is authoring syntax, not surface). The dynamic surface
// is read live:
//   * godot.* classes/methods/properties/signals  -> Godot.ClassDB introspection
//     (engine snake_case names — exactly what `obj.Call/Get/Set` route to from Lua)
//   * godot.* enums/constants/statics + the class set -> reflection over GodotSharp
//     (C# PascalCase — exactly what GodotBinder.Resolve exposes: godot.Timer.X.Idle)
//   * std.* types -> reflection over [LuaObject]/[LuaMember] (Lua-CSharp)
//   * capability apis (input/scene/save/sql) -> walking the live LuaTables Loader builds
//   * safe primitives / hooks / api names -> the same arrays the runtime itself uses
// So a new Godot version, a new [LuaObject] type, or a new api arm flows into the spec
// with zero registration. Must run inside a live Godot instance (ClassDB requires it):
//   godot --headless --path . -- --emit-api <outDir>
public static class EvaluateDocs
{
    public static void Emit(string outDir, Action<string>? log = null)
    {
        log ??= _ => { };
        var spec = Collect(log);

        var specDir = Path.GetFullPath(outDir);
        var catsDir = Path.Combine(specDir, "luacats");
        Directory.CreateDirectory(specDir);
        Directory.CreateDirectory(catsDir);

        File.WriteAllText(Path.Combine(specDir, "evaluate-api.json"), JsonWriter.Write(spec));
        File.WriteAllText(Path.Combine(catsDir, "std.lua"), LuaCats.Std(spec));
        File.WriteAllText(Path.Combine(catsDir, "evt-apis.lua"), LuaCats.Apis(spec));
        File.WriteAllText(Path.Combine(catsDir, "godot-full.lua"), LuaCats.Godot(spec, spec.Godot.Classes));
        var core = CoreClasses(spec);
        foreach (var c in core) c.Core = true;       // lets Markdown show core in full, rest as an index
        File.WriteAllText(Path.Combine(catsDir, "godot-core.lua"), LuaCats.Godot(spec, core));
        File.WriteAllText(Path.Combine(specDir, "evaluate-api.md"), Markdown.Write(spec));

        log($"docs: wrote api spec for Evaluate {spec.EvaluateVersion} / Godot {spec.GodotVersion} to {specDir}");
        log($"docs: {spec.Godot.Classes.Count} godot classes ({core.Count} in core), " +
            $"{spec.Std.Count} std types, {spec.Apis.Count} apis, {spec.Godot.Structs.Count} structs");
    }

    // ---- collection -----------------------------------------------------------

    private static Spec Collect(Action<string> log)
    {
        var spec = new Spec
        {
            EvaluateVersion = EvaluateVersionString(),
            GodotVersion = GodotVersionString(),
            SafePrimitives = Loader.SafeGlobals.Concat(new[] { "print" }).OrderBy(s => s).ToList(),
            WithheldGlobals = new() { "os", "io", "pcall" },
            Hooks = new Hooks { System = Loader.SystemHooks.ToList(), Node = Loader.NodeHooks.ToList() },
            Frontmatter = FrontmatterDoc(),
            Std = CollectStd(log),
            Apis = CollectApis(log),
            Godot = CollectGodot(log),
        };
        return spec;
    }

    private static string EvaluateVersionString()
    {
        var asm = typeof(EvaluateDocs).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? asm.GetName().Version?.ToString() ?? "unknown";
    }

    private static string GodotVersionString()
    {
        try { return Engine.GetVersionInfo()["string"].AsString(); }
        catch { return "unknown"; }
    }

    private static FrontmatterDocModel FrontmatterDoc() => new()
    {
        // The authoring grammar (the one inherently-static part: it is syntax, not API
        // surface). The *values* these keys accept are the dynamic surface above:
        // `apis:` <- Apis, `register:` <- Hooks, `returns:` <- the get/set grammar.
        Keys = new() { "config", "apis", "register", "returns", "assets", "scenes" },
        ReturnsGrammar = "<name>  |  <name>: 'get set <type>'  (read-only omits 'set')",
    };

    // ---- std (Evaluate-authored [LuaObject] types) ----------------------------

    private static List<StdType> CollectStd(Action<string> log)
    {
        var result = new List<StdType>();
        var asm = typeof(Std).Assembly;
        // Map a LuaObject type to its std.* constructor key via the same naming the
        // hand-built factory uses (EvaluateVec3 -> vec3, EvaluateLinkedList -> linked_list).
        var byKey = asm.GetTypes()
            .Where(IsLuaObject)
            .ToDictionary(t => SnakeCase(t.Name.StartsWith("Evaluate") ? t.Name["Evaluate".Length..] : t.Name));

        // Walk the live `std` table so the namespace is exactly what scripts see; enrich
        // each factory with its backing [LuaObject] type's members where one matches.
        var std = Std.Build();
        foreach (var kv in std)
        {
            var name = kv.Key.Read<string>();
            if (!byKey.TryGetValue(name, out var type))
            {
                result.Add(new StdType { Name = name, Ctor = "(...)" });
                continue;
            }
            result.Add(DescribeLuaObject(name, type));
        }
        log($"docs: collected {result.Count} std types");
        return result.OrderBy(s => s.Name).ToList();
    }

    private static StdType DescribeLuaObject(string name, Type type)
    {
        var st = new StdType { Name = name, Ctor = ConstructorSignature(type) };
        foreach (var m in type.GetMembers(BindingFlags.Public | BindingFlags.Instance))
        {
            var lua = LuaMemberName(m);
            if (lua is null) continue;
            if (m is PropertyInfo p)
                st.Fields.Add(new Field { Name = lua, Type = LuaType(p.PropertyType), ReadOnly = !p.CanWrite });
            else if (m is MethodInfo mi)
                st.Methods.Add(new Method
                {
                    Name = lua,
                    Args = mi.GetParameters().Select(pi => new Param { Name = pi.Name ?? "arg", Type = LuaType(pi.ParameterType) }).ToList(),
                    Returns = mi.ReturnType == typeof(void) ? "void" : LuaType(mi.ReturnType),
                });
        }
        return st;
    }

    private static string ConstructorSignature(Type type)
    {
        var ctor = type.GetConstructors().Where(c => c.GetParameters().Length > 0)
                       .OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is null) return "()";
        return "(" + string.Join(", ", ctor.GetParameters().Select(p => p.Name)) + ")";
    }

    // ---- capability apis ------------------------------------------------------

    private static List<ApiNamespace> CollectApis(Action<string> log)
    {
        var result = new List<ApiNamespace>();
        var loader = new Loader(_ => "", _ => { });
        foreach (var (name, table) in loader.DocApiTables())
        {
            var api = new ApiNamespace { Name = name };
            foreach (var kv in table)
                api.Members.Add(kv.Key.Read<string>());
            api.Members.Sort();
            result.Add(api);
        }
        // `world` is a wrapped Godot node, not a table — documented as such.
        if (Loader.ApiNames.Contains("world"))
            result.Add(new ApiNamespace
            {
                Name = "world",
                Note = "The persistent global-root Node (a wrapped godot instance, not a table). " +
                       "Use any Node method/property — e.g. world:add_child(node). Survives scene switches.",
            });
        log($"docs: collected {result.Count} capability apis");
        return result.OrderBy(a => a.Name).ToList();
    }

    // ---- godot surface --------------------------------------------------------

    private static readonly Assembly GodotAsm = typeof(GodotObject).Assembly;

    private static GodotSurface CollectGodot(Action<string> log)
    {
        var surface = new GodotSurface();

        // Class set = the C# types godot.<Type> resolves (GodotBinder.Resolve). Instance
        // members come from the live engine ClassDB by the SAME class name.
        var classTypes = GodotAsm.GetTypes()
            .Where(t => t.Namespace == "Godot" && !t.IsNested && typeof(GodotObject).IsAssignableFrom(t))
            .OrderBy(t => t.Name);

        foreach (var type in classTypes)
        {
            var cls = new GClass
            {
                Name = type.Name,
                Parent = NearestGodotParent(type),
                Instantiable = !type.IsAbstract,
            };
            if (ClassDB.ClassExists(type.Name))
            {
                cls.Methods = ClassMethods(type.Name, staticMethods: false);
                cls.StaticMethods = ReflectStatics(type);   // statics use C# reflection (binder's path)
                cls.Properties = ClassProperties(type.Name);
                cls.Signals = ClassSignals(type.Name);
            }
            cls.Enums = NestedEnums(type);
            cls.Constants = StaticIntConstants(type);
            surface.Classes.Add(cls);
        }

        // Top-level enums (godot.Key.Space, godot.MouseButton.Left, …).
        foreach (var e in GodotAsm.GetTypes().Where(t => t.Namespace == "Godot" && !t.IsNested && t.IsEnum).OrderBy(t => t.Name))
            surface.GlobalEnums.Add(EnumDoc(e));

        // Variant struct types (Vector*, Color, Quaternion, Transform*, …) — marshalled
        // as named-field / positional tables, not godot.<Type> handles.
        foreach (var s in StructTypes())
            surface.Structs.Add(s);

        log($"docs: collected {surface.Classes.Count} godot classes, {surface.GlobalEnums.Count} global enums, {surface.Structs.Count} structs");
        return surface;
    }

    private static string? NearestGodotParent(Type type)
    {
        var b = type.BaseType;
        while (b is not null && b != typeof(object))
        {
            if (typeof(GodotObject).IsAssignableFrom(b)) return b.Name;
            b = b.BaseType;
        }
        return null;
    }

    private static List<Method> ClassMethods(string cls, bool staticMethods)
    {
        var list = new List<Method>();
        foreach (Godot.Collections.Dictionary m in ClassDB.ClassGetMethodList(cls, noInheritance: true))
        {
            var flags = (MethodFlags)(m.ContainsKey("flags") ? m["flags"].AsInt64() : 0);
            if ((flags & MethodFlags.Virtual) != 0) continue;          // virtuals are overridden, not called
            if (((flags & MethodFlags.Static) != 0) != staticMethods) continue;
            var name = m["name"].AsString();
            if (name.StartsWith("_")) continue;                        // internal/virtual leftovers
            list.Add(new Method
            {
                Name = name,
                Args = ReadArgs(m),
                Returns = m.ContainsKey("return") ? FriendlyType(m["return"].AsGodotDictionary(), forReturn: true) : "void",
                IsStatic = (flags & MethodFlags.Static) != 0,
            });
        }
        return list.OrderBy(x => x.Name).ToList();
    }

    private static List<Param> ReadArgs(Godot.Collections.Dictionary descriptor)
    {
        var args = new List<Param>();
        if (!descriptor.ContainsKey("args")) return args;
        int i = 0;
        foreach (var a in descriptor["args"].AsGodotArray())
        {
            var ad = a.AsGodotDictionary();
            var n = ad.ContainsKey("name") ? ad["name"].AsString() : "";
            args.Add(new Param { Name = string.IsNullOrEmpty(n) ? $"arg{i}" : n, Type = FriendlyType(ad, forReturn: false) });
            i++;
        }
        return args;
    }

    private static List<Prop> ClassProperties(string cls)
    {
        var list = new List<Prop>();
        foreach (Godot.Collections.Dictionary p in ClassDB.ClassGetPropertyList(cls, noInheritance: true))
        {
            var usage = (PropertyUsageFlags)(p.ContainsKey("usage") ? p["usage"].AsInt64() : 0);
            if ((usage & (PropertyUsageFlags.Group | PropertyUsageFlags.Category | PropertyUsageFlags.Subgroup)) != 0) continue;
            var name = p.ContainsKey("name") ? p["name"].AsString() : "";
            if (name.Length == 0 || name.Contains('/')) continue;      // separators / per-feature sub-props
            var type = (Variant.Type)(p.ContainsKey("type") ? p["type"].AsInt64() : 0);
            if (type == Variant.Type.Nil) continue;
            list.Add(new Prop { Name = name, Type = FriendlyType(p, forReturn: false), ReadOnly = (usage & PropertyUsageFlags.ReadOnly) != 0 });
        }
        return list.OrderBy(x => x.Name).ToList();
    }

    private static List<Signal> ClassSignals(string cls)
    {
        var list = new List<Signal>();
        foreach (Godot.Collections.Dictionary s in ClassDB.ClassGetSignalList(cls, noInheritance: true))
            list.Add(new Signal { Name = s["name"].AsString(), Args = ReadArgs(s) });
        return list.OrderBy(x => x.Name).ToList();
    }

    private static List<Method> ReflectStatics(Type type)
    {
        // Mirror GodotBinder.Resolve's static binding: public static, non-special,
        // non-generic, all params + return marshallable. PascalCase (the reflected name).
        var list = new List<Method>();
        foreach (var group in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                  .Where(m => !m.IsSpecialName && !m.IsGenericMethod)
                                  .Where(m => m.GetParameters().All(p => GodotBinder.CanMarshal(p.ParameterType))
                                           && (m.ReturnType == typeof(void) || GodotBinder.CanMarshal(m.ReturnType)))
                                  .GroupBy(m => m.Name))
        {
            var m = group.First();
            list.Add(new Method
            {
                Name = m.Name,
                Args = m.GetParameters().Select(p => new Param { Name = p.Name ?? "arg", Type = LuaType(p.ParameterType) }).ToList(),
                Returns = m.ReturnType == typeof(void) ? "void" : LuaType(m.ReturnType),
                IsStatic = true,
            });
        }
        return list.OrderBy(x => x.Name).ToList();
    }

    private static List<GEnum> NestedEnums(Type type) =>
        type.GetNestedTypes(BindingFlags.Public).Where(t => t.IsEnum).Select(EnumDoc).OrderBy(e => e.Name).ToList();

    private static GEnum EnumDoc(Type e)
    {
        var ge = new GEnum { Name = e.Name };
        foreach (var n in Enum.GetNames(e))
            ge.Constants[n] = Convert.ToInt64(Convert.ChangeType(Enum.Parse(e, n), typeof(long)));
        return ge;
    }

    private static Dictionary<string, long> StaticIntConstants(Type type)
    {
        // Standalone int constants the binder exposes via type.GetField (literal/initonly),
        // excluding enum-typed fields (covered by NestedEnums).
        var d = new Dictionary<string, long>();
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (!(f.IsLiteral || f.IsInitOnly)) continue;
            var ft = f.FieldType;
            if (ft.IsEnum) continue;
            if (ft == typeof(int) || ft == typeof(long) || ft == typeof(uint) || ft == typeof(byte))
                try { d[f.Name] = Convert.ToInt64(f.GetValue(null)); } catch { }
        }
        return d;
    }

    private static List<GStruct> StructTypes()
    {
        var excluded = new HashSet<string> { "Nil", "Bool", "Int", "Float", "String", "StringName", "NodePath",
            "Object", "Callable", "Signal", "Rid", "Dictionary", "Array", "Max" };
        var result = new List<GStruct>();
        foreach (var name in Enum.GetNames(typeof(Variant.Type)))
        {
            if (excluded.Contains(name) || name.StartsWith("Packed")) continue;
            var t = GodotAsm.GetType("Godot." + name);
            if (t is null || !t.IsValueType || t.IsEnum || t.IsPrimitive) continue;
            var comps = StructComponents(t);
            if (comps.Count == 0) continue;
            result.Add(new GStruct
            {
                Name = name,
                Fields = comps.Select(c => c.key).ToList(),
                Composite = comps.Any(c => !c.scalar),
            });
        }
        return result;
    }

    private static List<(string key, bool scalar)> StructComponents(Type t)
    {
        var comps = new List<(string, bool)>();
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            comps.Add((f.Name.ToLowerInvariant(), IsScalar(f.FieldType)));
        if (comps.Count == 0)
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
                comps.Add((p.Name.ToLowerInvariant(), IsScalar(p.PropertyType)));
        return comps;
    }

    private static bool IsScalar(Type t) =>
        t == typeof(float) || t == typeof(double) || t == typeof(int) || t == typeof(long) || t == typeof(byte);

    // Curated "core" allowlist for the default LuaCATS file: instantiable classes plus
    // the spine of base classes they inherit from. Derived automatically from the dump
    // (no literal list), so it tracks the engine; godot-full.lua is the escape hatch.
    private static List<GClass> CoreClasses(Spec spec)
    {
        var byName = spec.Godot.Classes.ToDictionary(c => c.Name);
        var keep = new HashSet<string>();
        foreach (var c in spec.Godot.Classes.Where(c => c.Instantiable && byName.ContainsKey("Node") && InheritsFrom(c, "Node", byName)))
        {
            keep.Add(c.Name);
            for (var p = c.Parent; p is not null && byName.TryGetValue(p, out var pc); p = pc.Parent) keep.Add(p);
        }
        // Always include the universally useful singletons/utility roots even if not Node-derived.
        foreach (var n in new[] { "Object", "RefCounted", "Resource", "Input", "OS", "Time", "Engine", "ProjectSettings" })
            if (byName.ContainsKey(n)) keep.Add(n);
        return spec.Godot.Classes.Where(c => keep.Contains(c.Name)).ToList();
    }

    private static bool InheritsFrom(GClass c, string ancestor, Dictionary<string, GClass> byName)
    {
        for (var p = c.Name; p is not null && byName.TryGetValue(p, out var pc); p = pc.Parent)
            if (p == ancestor) return true;
        return false;
    }

    // ---- shared type mapping --------------------------------------------------

    // From an engine descriptor dict (type int + class_name) to a friendly Lua-facing type.
    private static string FriendlyType(Godot.Collections.Dictionary d, bool forReturn)
    {
        var t = (Variant.Type)(d.ContainsKey("type") ? d["type"].AsInt64() : 0);
        var cn = d.ContainsKey("class_name") ? d["class_name"].AsString() : "";
        return FriendlyType(t, cn, forReturn);
    }

    private static string FriendlyType(Variant.Type t, string className, bool forReturn)
    {
        switch (t)
        {
            case Variant.Type.Nil: return forReturn ? "void" : "any";
            case Variant.Type.Bool: return "boolean";
            case Variant.Type.Int: return "integer";
            case Variant.Type.Float: return "number";
            case Variant.Type.String:
            case Variant.Type.StringName:
            case Variant.Type.NodePath: return "string";
            case Variant.Type.Object: return string.IsNullOrEmpty(className) ? "Object" : className;
            case Variant.Type.Dictionary:
            case Variant.Type.Array: return "table";
            case Variant.Type.Callable: return "function";
            default:
                var s = t.ToString();
                if (s.StartsWith("Packed")) return "table";
                return string.IsNullOrEmpty(className) ? s : className;
        }
    }

    // From a C# type (statics / std members) to a friendly Lua-facing type.
    private static string LuaType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(void)) return "void";
        if (t == typeof(bool)) return "boolean";
        if (t == typeof(string) || t == typeof(StringName) || t == typeof(NodePath)) return "string";
        if (t == typeof(float) || t == typeof(double)) return "number";
        if (t == typeof(int) || t == typeof(long) || t == typeof(uint) || t == typeof(byte) || t == typeof(short)) return "integer";
        if (t == typeof(LuaValue)) return "any";
        if (t == typeof(Variant)) return "any";
        if (t.IsEnum) return "integer";
        if (t == typeof(Godot.Collections.Array) || t == typeof(Godot.Collections.Dictionary)) return "table";
        if (typeof(GodotObject).IsAssignableFrom(t)) return t.Name;
        if (t.Namespace == "Godot" && t.IsValueType) return t.Name;   // a struct
        if (IsLuaObject(t)) return "std." + SnakeCase(t.Name.StartsWith("Evaluate") ? t.Name["Evaluate".Length..] : t.Name);
        return "any";
    }

    private static bool IsLuaObject(Type t) =>
        t.GetCustomAttributesData().Any(a => a.AttributeType.Name == "LuaObjectAttribute");

    private static string? LuaMemberName(MemberInfo m)
    {
        var attr = m.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == "LuaMemberAttribute");
        if (attr is null) return null;
        if (attr.ConstructorArguments.Count > 0 && attr.ConstructorArguments[0].Value is string s) return s;
        return SnakeCase(m.Name);
    }

    private static string SnakeCase(string s)
    {
        var sb = new StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsUpper(c) && i > 0 && !char.IsUpper(s[i - 1])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
