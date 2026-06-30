using System.Collections.Generic;

namespace Evaluate;

// The in-memory API-spec model EvaluateDocs collects and the writers (JSON / LuaCATS /
// Markdown) render. Plain POCOs so System.Text.Json serializes them directly.

public sealed class Spec
{
    public string EvaluateVersion { get; set; } = "";
    public string GodotVersion { get; set; } = "";
    public List<string> SafePrimitives { get; set; } = new();
    public List<string> WithheldGlobals { get; set; } = new();
    public Hooks Hooks { get; set; } = new();
    public FrontmatterDocModel Frontmatter { get; set; } = new();
    public List<StdType> Std { get; set; } = new();
    public List<ApiNamespace> Apis { get; set; } = new();
    public GodotSurface Godot { get; set; } = new();
}

public sealed class Hooks
{
    public List<string> System { get; set; } = new();
    public List<string> Node { get; set; } = new();
}

public sealed class FrontmatterDocModel
{
    public List<string> Keys { get; set; } = new();
    public string ReturnsGrammar { get; set; } = "";
    public string ParamsGrammar { get; set; } = "";
}

public sealed class StdType
{
    public string Name { get; set; } = "";
    public string Ctor { get; set; } = "";
    public List<Field> Fields { get; set; } = new();
    public List<Method> Methods { get; set; } = new();
}

public sealed class Field
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool ReadOnly { get; set; }
}

public sealed class Param
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}

public sealed class Method
{
    public string Name { get; set; } = "";
    public List<Param> Args { get; set; } = new();
    public string Returns { get; set; } = "void";
    public bool IsStatic { get; set; }
}

public sealed class ApiNamespace
{
    public string Name { get; set; } = "";
    public List<string> Members { get; set; } = new();
    public string? Note { get; set; }
}

public sealed class GodotSurface
{
    public List<GClass> Classes { get; set; } = new();
    public List<GEnum> GlobalEnums { get; set; } = new();
    public List<GStruct> Structs { get; set; } = new();
}

public sealed class GClass
{
    public string Name { get; set; } = "";
    public string? Parent { get; set; }
    public bool Instantiable { get; set; }
    public bool Core { get; set; }
    public List<Method> Methods { get; set; } = new();
    public List<Method> StaticMethods { get; set; } = new();
    public List<Prop> Properties { get; set; } = new();
    public List<Signal> Signals { get; set; } = new();
    public List<GEnum> Enums { get; set; } = new();
    public Dictionary<string, long> Constants { get; set; } = new();
}

public sealed class Prop
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool ReadOnly { get; set; }
}

public sealed class Signal
{
    public string Name { get; set; } = "";
    public List<Param> Args { get; set; } = new();
}

public sealed class GEnum
{
    public string Name { get; set; } = "";
    public Dictionary<string, long> Constants { get; set; } = new();
}

public sealed class GStruct
{
    public string Name { get; set; } = "";
    public List<string> Fields { get; set; } = new();
    public bool Composite { get; set; }
}
