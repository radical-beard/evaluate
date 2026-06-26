# Discovering the API (don't guess names)

The complete `godot.*` + `std.*` + capability surface is **generated** from the live engine
and shipped alongside this skill. It is exact for the Godot + EvaLuate version it was built
against — prefer it over memory or generic Godot docs.

## The generated spec files

In an EvaLuate download these sit under `spec/` (next to this `skill/`):

| File | Use |
|------|-----|
| `spec/evaluate-api.md` | Human/LLM-readable reference. **Read this first** to find a class, method, hook, or struct. |
| `spec/evaluate-api.json` | The full machine-readable spec (every class, method, property, signal, enum, constant). Grep/parse it for an exact signature. |
| `spec/luacats/godot-core.lua` | LuaCATS defs for the common classes (Node/Node2D/Node3D/CharacterBody*/Area*/Timer/Input/OS/…). |
| `spec/luacats/godot-full.lua` | LuaCATS defs for **every** engine class (opt-in; large). |
| `spec/luacats/std.lua`, `spec/luacats/evt-apis.lua` | LuaCATS for `std.*` and the capability apis. |

### IDE autocomplete

Point [lua-language-server](https://luals.github.io/) at the `luacats/` folder (e.g. add it
to `Lua.workspace.library` in your editor settings, or drop the files in your workspace).
You then get autocomplete and hover over the whole `godot.*` and `std.*` surface — typed
methods, properties, signals, enums.

## Regenerating (it adapts automatically)

The spec is produced by EvaLuate itself — nothing is hand-written or hard-coded — so it
tracks new Godot versions, new engine classes (including GDExtension), and new EvaLuate
features with no manual updating. Regenerate for your project:

```
godot --headless --path . -- --emit-api downloads/spec
```

Run it whenever you upgrade Godot or EvaLuate.

## Naming conventions (so a name resolves)

- **Instance methods & properties → engine `snake_case`:** `node:get_node("X")`,
  `node.global_position`, `body:move_and_slide()`, `timer:emit_signal("timeout")`.
  `node:GetNode()` (PascalCase) will **not** resolve on an instance.
- **Constructors, enums, constants, static methods → C# `PascalCase`:**
  `godot.Timer.new()`, `godot.Key.Space`, `godot.MouseButton.Left`,
  `godot.Timer.TimerProcessCallback.Idle`, `godot.OS.GetName()`.
- **Signals:** connect from Lua with `obj:connect("signal_name", fn)` (0–6 args). The
  callback runs on the main thread.
- **Structs cross as tables:** reading a `Vector3`/`Transform3D` gives a table
  (`{x=,y=,z=}` / named fields); assigning a table or `std.vec3` back rebuilds the struct.

If a name isn't in the generated spec for your Godot build, it isn't available — check
`spec/evaluate-api.md`.
