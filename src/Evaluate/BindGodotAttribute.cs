using System;

namespace Evaluate;

// Mark a partial class to get a pre-baked, reflection-free Lua binding for the
// given Godot type's public static methods. The generator emits a static
// Create() returning a LuaTable. See generator/BindGodotGenerator.cs.
[AttributeUsage(AttributeTargets.Class)]
public sealed class BindGodotAttribute : Attribute
{
    public BindGodotAttribute(Type godotType) => GodotType = godotType;
    public Type GodotType { get; }
}

// Example: pre-bake Godot.OS. `OsBinding.Create()` is generated.
[BindGodot(typeof(Godot.OS))]
public partial class OsBinding { }
