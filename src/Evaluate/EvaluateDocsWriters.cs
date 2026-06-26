using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Evaluate;

// Renders the collected Spec into the three consumer-facing formats. None of these
// encode any API knowledge themselves — they are pure projections of the live dump.

internal static class JsonWriter
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write(Spec spec) => JsonSerializer.Serialize(spec, Opts) + "\n";
}

// LuaCATS (lua-language-server `---@meta`) definitions: drop into a workspace and the
// Lua LSP gives autocomplete + hover over the whole godot.* + std.* + capability surface.
internal static class LuaCats
{
    private static readonly HashSet<string> LuaKeywords = new()
    {
        "and","break","do","else","elseif","end","false","for","function","goto","if",
        "in","local","nil","not","or","repeat","return","then","true","until","while",
    };

    // A friendly type -> a LuaLS type. Primitives pass through; already-namespaced names
    // (std.vec3) pass through; everything else is a Godot class/struct (Godot.Vector3).
    private static string Cats(string f) => f switch
    {
        "void" => "nil",
        "boolean" or "integer" or "number" or "string" or "table" or "any" or "function" => f,
        _ when f.Contains('.') => f,
        _ => "Godot." + f,
    };

    private static string Ident(string name) =>
        LuaKeywords.Contains(name) || name.Length == 0 ? "_" + name : name;

    private static string FunType(Method m)
    {
        var ps = string.Join(", ", m.Args.Select(a => $"{Ident(a.Name)}: {Cats(a.Type)}"));
        var ret = m.Returns == "void" ? "" : $": {Cats(m.Returns)}";
        return $"fun({ps}){ret}";
    }

    // A method rendered as a typed field — `fun(self, …)` — NOT a `function … end` stub.
    // This carries no `local`, so one file holds thousands of classes without tripping
    // Lua's 200-locals-per-chunk limit (which `local _Class = {}` per class would).
    private static void MethodField(StringBuilder sb, string ownerType, Method m)
    {
        var ps = new List<string> { $"self: {ownerType}" };
        ps.AddRange(m.Args.Select(a => $"{Ident(a.Name)}: {Cats(a.Type)}"));
        var ret = m.Returns == "void" ? "" : $": {Cats(m.Returns)}";
        Fld(sb, m.Name, $"fun({string.Join(", ", ps)}){ret}");
    }

    // Emit a ---@field line unless the name is a Lua keyword (rare engine members like
    // `repeat`/`function`); those remain documented in the JSON and Markdown.
    private static void Fld(StringBuilder sb, string name, string type)
    {
        if (!LuaKeywords.Contains(name)) sb.Append($"---@field {name} {type}\n");
    }

    public static string Std(Spec spec)
    {
        var sb = new StringBuilder();
        sb.Append("---@meta\n-- EvaLuate std.* — pure C#-backed data types, always available.\n\n");
        sb.Append("---@class std\n");
        foreach (var t in spec.Std) sb.Append($"---@field {t.Name} fun(...): std.{t.Name}  -- {t.Name}{t.Ctor}\n");
        sb.Append("std = {}\n\n");

        foreach (var t in spec.Std)
        {
            sb.Append($"---@class std.{t.Name}\n");
            foreach (var f in t.Fields) Fld(sb, f.Name, Cats(f.Type));
            foreach (var m in t.Methods) MethodField(sb, $"std.{t.Name}", m);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public static string Apis(Spec spec)
    {
        var sb = new StringBuilder();
        sb.Append("---@meta\n");
        sb.Append("-- EvaLuate capability apis. A script reaches one only after declaring it in\n");
        sb.Append("-- frontmatter `apis:` (godot/std are ambient). Lifecycle hooks & frontmatter keys\n");
        sb.Append("-- are listed at the bottom for reference.\n\n");

        foreach (var api in spec.Apis)
        {
            if (api.Note != null) { sb.Append($"-- {api.Name}: {api.Note}\n"); continue; }
            sb.Append($"---@class evt.{api.Name}\n");
            foreach (var member in api.Members)
                sb.Append($"---@field {member} fun(...): any\n");
            sb.Append($"{api.Name} = {{}}\n\n");   // declared only when listed in `apis:`; typed here for completion
        }

        sb.Append("-- system hooks (register: on a .evt):   " + string.Join(", ", spec.Hooks.System) + "\n");
        sb.Append("-- node hooks   (register: on a .node.evt): " + string.Join(", ", spec.Hooks.Node) + "\n");
        sb.Append("-- frontmatter keys: " + string.Join(", ", spec.Frontmatter.Keys) + "\n");
        sb.Append("-- returns grammar: " + spec.Frontmatter.ReturnsGrammar + "\n");
        return sb.ToString();
    }

    public static string Godot(Spec spec, List<GClass> classes)
    {
        var present = new HashSet<string>(classes.Select(c => c.Name));
        var sb = new StringBuilder();
        sb.Append("---@meta\n-- EvaLuate godot.* — the engine surface for this Godot build.\n");
        sb.Append("-- Instance members are engine snake_case (node:get_node(), node.position);\n");
        sb.Append("-- enums/constants/new are C# PascalCase (godot.Timer.new(), godot.Key.Space).\n\n");

        // The `godot` ambient table: one field per resolvable type -> its static handle.
        sb.Append("---@class godot\n");
        foreach (var c in classes) sb.Append($"---@field {c.Name} Godot.{c.Name}.__type\n");
        foreach (var e in spec.GlobalEnumsFor(present)) sb.Append($"---@field {e.Name} Godot.{e.Name}\n");
        sb.Append("godot = {}\n\n");

        foreach (var c in classes)
        {
            // instance class (methods are typed fields — see MethodField; no locals)
            var ext = c.Parent != null && present.Contains(c.Parent) ? $" : Godot.{c.Parent}" : "";
            sb.Append($"---@class Godot.{c.Name}{ext}\n");
            foreach (var p in c.Properties) Fld(sb, p.Name, Cats(p.Type));
            foreach (var s in c.Signals) Fld(sb, s.Name, "Godot.Signal");
            foreach (var m in c.Methods) MethodField(sb, $"Godot.{c.Name}", m);

            // static handle: new + statics + constants + nested enums
            sb.Append($"---@class Godot.{c.Name}.__type\n");
            if (c.Instantiable) sb.Append($"---@field new fun(): Godot.{c.Name}\n");
            foreach (var sm in c.StaticMethods) Fld(sb, sm.Name, FunType(sm));
            foreach (var k in c.Constants.Keys) Fld(sb, k, "integer");
            foreach (var e in c.Enums) Fld(sb, e.Name, $"Godot.{c.Name}.{e.Name}");

            foreach (var e in c.Enums)
            {
                sb.Append($"---@class Godot.{c.Name}.{e.Name}\n");
                foreach (var k in e.Constants.Keys) Fld(sb, k, "integer");
            }
            sb.Append('\n');
        }

        // global enums (godot.Key.Space, …)
        foreach (var e in spec.GlobalEnumsFor(present))
        {
            sb.Append($"---@class Godot.{e.Name}\n");
            foreach (var k in e.Constants.Keys) Fld(sb, k, "integer");
            sb.Append('\n');
        }
        return sb.ToString();
    }
}

// A human/LLM-readable reference, generated from the same Spec. Godot classes are huge,
// so core classes get full tables and the rest an index (full detail lives in the JSON
// and LuaCATS files).
internal static class Markdown
{
    public static string Write(Spec spec)
    {
        var sb = new StringBuilder();
        sb.Append($"# EvaLuate Lua API reference\n\n");
        sb.Append($"_Generated by `--emit-api` for **Evaluate {spec.EvaluateVersion}** on **Godot {spec.GodotVersion}**. ");
        sb.Append("Do not edit by hand — regenerate with `godot --headless --path . -- --emit-api downloads/spec`._\n\n");
        sb.Append("Every `.evt`/`.node.evt` script runs in a capability sandbox. Always available: `std`, `godot`, ");
        sb.Append("the safe language primitives, and (in node scripts) `self`. Everything else must be declared in ");
        sb.Append("frontmatter `apis:`. For IDE autocomplete, point lua-language-server at the `luacats/` files.\n\n");

        sb.Append("## Always-available primitives\n\n");
        sb.Append("`" + string.Join("`, `", spec.SafePrimitives) + "`\n\n");
        sb.Append("**Deliberately withheld** (absent from every sandbox): `" + string.Join("`, `", spec.WithheldGlobals) + "`.\n\n");

        sb.Append("## std.* (always available)\n\n");
        foreach (var t in spec.Std)
        {
            sb.Append($"### `std.{t.Name}{t.Ctor}`\n\n");
            if (t.Fields.Count > 0)
                sb.Append("Fields: " + string.Join(", ", t.Fields.Select(f => $"`{f.Name}: {f.Type}`" + (f.ReadOnly ? " (read-only)" : ""))) + "\n\n");
            foreach (var m in t.Methods)
                sb.Append($"- `:{m.Name}({Args(m.Args)})`" + (m.Returns != "void" ? $" → `{m.Returns}`" : "") + "\n");
            sb.Append('\n');
        }

        sb.Append("## Capability apis (declare in `apis:`)\n\n");
        foreach (var api in spec.Apis)
        {
            sb.Append($"### `{api.Name}`\n\n");
            if (api.Note != null) { sb.Append(api.Note + "\n\n"); continue; }
            foreach (var m in api.Members) sb.Append($"- `{api.Name}.{m}(...)`\n");
            sb.Append('\n');
        }

        sb.Append("## Lifecycle hooks (`register:`)\n\n");
        sb.Append("- **system** (`.evt`): " + string.Join(", ", spec.Hooks.System.Select(h => $"`{h}`")) + "\n");
        sb.Append("- **node** (`.node.evt`, `self`-bound): " + string.Join(", ", spec.Hooks.Node.Select(h => $"`{h}`")) + "\n\n");

        sb.Append("## Frontmatter keys\n\n");
        sb.Append("`" + string.Join("`, `", spec.Frontmatter.Keys) + "`\n\n");
        sb.Append($"`returns:` grammar — `{spec.Frontmatter.ReturnsGrammar}`\n\n");

        sb.Append("## Variant structs (named-field / positional tables)\n\n");
        sb.Append("Read as tables; assigned back type-aware. All-scalar structs use a positional array; ");
        sb.Append("composite structs use a `_type`-tagged table.\n\n");
        foreach (var s in spec.Godot.Structs)
            sb.Append($"- **{s.Name}** — {(s.Composite ? "composite `{ _type = \"" + s.Name + "\", … }`" : "positional `[" + string.Join(", ", s.Fields) + "]`")}: `{string.Join(", ", s.Fields)}`\n");
        sb.Append('\n');

        sb.Append("## godot.* classes\n\n");
        sb.Append($"{spec.Godot.Classes.Count} engine classes are available as `godot.<Type>`. ");
        sb.Append("Full per-class detail (methods, properties, signals, enums, constants) is in `evaluate-api.json` ");
        sb.Append("and the `luacats/` files. Core classes are detailed below; all others are indexed at the end.\n\n");

        foreach (var c in spec.Godot.Classes.Where(c => c.Core).OrderBy(c => c.Name))
            WriteClass(sb, c);

        sb.Append("### Full class index\n\n");
        foreach (var c in spec.Godot.Classes.OrderBy(c => c.Name))
            sb.Append($"- `{c.Name}`{(c.Parent != null ? $" : {c.Parent}" : "")} — " +
                      $"{c.Methods.Count} methods, {c.Properties.Count} props, {c.Signals.Count} signals" +
                      $"{(c.Instantiable ? ", `new`" : "")}\n");
        return sb.ToString();
    }

    private static void WriteClass(StringBuilder sb, GClass c)
    {
        sb.Append($"### `godot.{c.Name}`{(c.Parent != null ? $" : `{c.Parent}`" : "")}{(c.Instantiable ? " — `godot." + c.Name + ".new()`" : " (abstract)")}\n\n");
        if (c.Properties.Count > 0)
        {
            sb.Append("**Properties:** " + string.Join(", ", c.Properties.Select(p => $"`{p.Name}: {p.Type}`" + (p.ReadOnly ? " (ro)" : ""))) + "\n\n");
        }
        if (c.Methods.Count > 0)
        {
            sb.Append("**Methods:**\n");
            foreach (var m in c.Methods) sb.Append($"- `{m.Name}({Args(m.Args)})`" + (m.Returns != "void" ? $" → `{m.Returns}`" : "") + "\n");
            sb.Append('\n');
        }
        if (c.Signals.Count > 0)
            sb.Append("**Signals:** " + string.Join(", ", c.Signals.Select(s => $"`{s.Name}({Args(s.Args)})`")) + "\n\n");
        if (c.Enums.Count > 0)
            sb.Append("**Enums:** " + string.Join(", ", c.Enums.Select(e => $"`{e.Name}`")) + "\n\n");
    }

    private static string Args(List<Param> args) => string.Join(", ", args.Select(a => $"{a.Name}: {a.Type}"));
}

internal static class SpecExtensions
{
    // Global enums are emitted into both core and full LuaCATS regardless of class filter.
    public static IEnumerable<GEnum> GlobalEnumsFor(this Spec spec, HashSet<string> _) => spec.Godot.GlobalEnums;
}
