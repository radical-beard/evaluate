---
name: evaluate-scripting
description: >-
  Author and edit EvaLuate game scripts — sandboxed Lua (.evt / .node.evt) and
  TOML scene files that run inside Godot 4.6. Use whenever writing or modifying
  EvaLuate scripts: declaring frontmatter (apis/config/register/returns/params/scenes),
  wiring lifecycle hooks (on_start/on_update/on_ready/…), calling the godot.*,
  std.*, save, scene, sql, input or world APIs, or building .scene node trees.
  Covers the capability sandbox rules that make scripts fail if you skip them.
---

# Writing EvaLuate scripts

EvaLuate runs **Lua inside Godot**. Godot is the host; there is no `main`. You write
small scripts that the runtime auto-discovers, sandboxes, and hot-reloads. Game objects
**are** Godot nodes — you reach them through the ambient `godot.*` binding, not a separate
entity system.

## The one rule that trips everyone up: the capability sandbox

Each script runs in an environment that contains **only what it declares**, plus a few
always-available things. If you use something you didn't declare, the script errors
(errors surface loudly — `pcall` is withheld on purpose).

**Always available** (no declaration needed):
- `std.*` — pure data types (`std.vec3`, `std.vec2`, `std.color`, `std.vector`, `std.linked_list`).
- `godot.*` — every Godot type (`godot.Node3D.new()`, `godot.Timer`, `godot.Key.Space`).
- Language primitives: `pairs ipairs next type tostring tonumber error assert select
  string math table setmetatable getmetatable rawget rawset rawequal rawlen print`.
- `self` — in node scripts only (the node this script is attached to).
- `params` — in node scripts only (typed, per-instance values the scene supplies; see below).

**Must be declared** in frontmatter `apis:` before use:
- `input`, `world`, `scene`, `save`, `sql`.

**Never available** (deliberately removed): `os`, `io`, `pcall`. Don't reach for them.

> Full, version-exact API surface is in the generated spec — see
> [reference/api-discovery.md](reference/api-discovery.md). Read it before guessing a
> method name.

## The two kinds of script

### System script — `name.evt`
A conductor. Declares lifecycle hooks in `register:`. **Global** by default (runs in every
scene); add `scenes: [a, b]` to scope it to those scenes. No `self`.

```
---
config:
 - game.toml          # exposes config.game.*
apis:
 - save               # lets this script use `save`
register:
 - on_start           # hooks this script handles (each must be a global function below)
 - on_update
---
function on_start()
  local n = save.get("launch_count", 0) + 1
  save.set("launch_count", n)
  print("launch #" .. n .. ", title=" .. config.game.title)
end

function on_update(dt)
  -- runs every frame
end
```

### Node script — `name.node.evt`
Behavior for **one node**, attached via a `.scene` file (`script = "name.node.evt"`). Here
`self` IS that Godot node — no spawning. Uses node hooks (`on_attach`, `on_ready`, …).

```
---
config:
 - player.toml
apis:
 - input
register:
 - on_attach
 - on_update
---
function on_attach()
  self.position = std.vec3(config.player.start_pos)   -- TOML array -> Vector3
end

function on_update(dt)
  if input.is_down("right") then
    local p = self.position
    p.x = p.x + config.player.move_speed * dt
    self.position = p                                   -- assign back to persist
  end
end
```

#### Per-instance `params` (node scripts)
`config.*` is shared by every node; **`params.*` is per-node**. The node script declares
typed parameters in a `params:` block; the `.scene` file supplies values for each instance
via `params = {..}` (next to `position`). The script reads them through the `params` global.
A param with no default is **required** (the scene must supply it); the scene cannot pass a
param the script didn't declare, nor a value of the wrong type — both error at load.

```
--- enemy.node.evt
params:
  max_health: 100         # default 100 (number) — scene may override
  faction: "neutral"      # default "neutral"
  patrol_speed: number    # typed + REQUIRED — the scene must supply it
register:
 - on_attach
---
function on_attach()
  self:set_meta("hp", params.max_health)   -- 60 if the scene overrode it, else 100
end
```
```toml
[nodes.Enemy]
type = "Node3D"
script = "enemy.node.evt"
position = [3, 0, 0]
params = { patrol_speed = 2.5, max_health = 60 }   # faction omitted -> default "neutral"
```

### Scene file — `name.scene` (TOML)
A node tree. Table nesting **is** the tree. See
[reference/scene-grammar.md](reference/scene-grammar.md).

```
[nodes.Player]
type = "Node3D"
script = "player.node.evt"     # attach a node script

[nodes.Player.Camera]          # child of Player
type = "Camera3D"
position = [0, 2, -4]          # property: array -> Vector3
```

## How members map (so you pick the right names)

- **Instance methods & properties are engine `snake_case`**: `node:get_node("X")`,
  `node.position`, `timer:emit_signal("timeout")`. (They route through the engine, which
  uses snake_case — `node:GetNode()` will NOT work.)
- **Constructors, enums and constants are C# `PascalCase`**: `godot.Timer.new()`,
  `godot.Key.Space`, `godot.Timer.TimerProcessCallback.Idle`, `godot.OS.GetName()`.
- **Vectors/colors/transforms** cross the boundary as tables: read a `Vector3` and you get
  `{x=,y=,z=}` (or `std.vec3`); assign a table/`std.vec3` back and it rebuilds the struct.

## Workflow for an agent using this skill

1. Decide the script kind (system vs node) and which scenes it belongs to.
2. Write the frontmatter **first** — declare every `api` and `config` you'll touch and every
   hook you implement. Under-declaring is the #1 cause of failures
   ([common-mistakes.md](common-mistakes.md)).
3. Look up exact API names in the generated spec
   ([reference/api-discovery.md](reference/api-discovery.md)); for IDE autocomplete, load the
   LuaCATS files into lua-language-server.
4. Implement each declared hook as a global function (`function on_update(dt) … end`).

## Reference

- [reference/frontmatter-contract.md](reference/frontmatter-contract.md) — every frontmatter key.
- [reference/sandbox-rules.md](reference/sandbox-rules.md) — capabilities, `require`/`returns`, what's withheld.
- [reference/lifecycle-hooks.md](reference/lifecycle-hooks.md) — every hook, args, and when it fires.
- [reference/scene-grammar.md](reference/scene-grammar.md) — `.scene` TOML rules.
- [reference/api-discovery.md](reference/api-discovery.md) — the generated, version-exact API spec.
- [common-mistakes.md](common-mistakes.md) — the sandbox errors and their fixes.
- [examples/](examples/) — runnable, verbatim scripts to copy from.
