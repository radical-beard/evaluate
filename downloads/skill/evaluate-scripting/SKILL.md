---
name: evaluate-scripting
description: >-
  Author and edit EvaLuate game scripts — sandboxed Lua (.evt / .behavior.evt /
  .statemachine.evt) and TOML scene files that run inside Godot 4.6. Use whenever
  writing or modifying EvaLuate scripts: declaring frontmatter (apis/config/register/
  returns/params/require/scenes/assets/properties/behaviors/machines/attributes/abilities),
  wiring lifecycle hooks (on_start/on_update/on_attach/…), declaring Godot classes and the
  input/save/scene/sql/world apis as modules, loading assets, building .scene node trees,
  or authoring state machines and GAS-lite abilities. Covers the capability sandbox rules
  that make scripts fail if you skip them.
---

# Writing EvaLuate scripts

EvaLuate runs **Lua inside Godot**. Godot is the host; there is no `main`. You write
small scripts that the runtime auto-discovers, sandboxes, and hot-reloads. Game objects
**are** Godot nodes — you reach the engine through **declared class modules**, not a
separate entity system.

## The one rule that trips everyone up: the capability sandbox

Each script runs in an environment that contains **only what it declares**, plus a few
always-available things. If you use something you didn't declare, the script errors
(errors surface loudly — `pcall` is withheld on purpose).

**Always available** (no declaration needed):
- `std.*` — pure data types (`std.vec3`, `std.vec2`, `std.color`, `std.vector`, `std.linked_list`).
- Language primitives: `pairs ipairs next type tostring tonumber error assert select
  string math table setmetatable getmetatable rawget rawset rawequal rawlen print`.
- `self` — in node-attached scripts only (the node this script is attached to).
- `params` — in node-attached scripts only (typed, per-instance values the scene supplies; see below).

**Everything else is declared** in frontmatter `apis:`, each entry injected as its own bare
global table (resolution order: framework service → host-registered api → Godot class/enum):
- framework services: `input`, `world`, `scene`, `save`, `sql`;
- host-registered C# apis (the game registers them via `runtime.RegisterApi`);
- **any Godot class or enum**: `apis: [Input, Node3D, Key]` → `Input.GetJoyAxis(...)`,
  `Node3D.new()`, `Key.Space`. There is **no ambient `godot.*` table anymore.** Declaring
  gates the class table only — instances you get from `self`, `get_node`, signal args or
  return values expose their members regardless.

**Never available**: `os`, `io`, `pcall` (deliberately removed), and the imperative IO
classes `ResourceLoader` / `ResourceSaver` / `FileAccess` / `DirAccess` — assets come from
frontmatter `assets:` (a map of `name: "path"`, eagerly loaded, hot-reloaded, injected as
the ambient `assets` table), persistence from `save`/`sql`.

> Full, version-exact API surface is in the generated spec — see
> [reference/api-discovery.md](reference/api-discovery.md). Read it before guessing a
> method name.

## The kinds of script

### System script — `name.evt`
A conductor. Declares lifecycle hooks in `register:`. **Global** by default (runs in every
scene); add `scenes: [a, b]` to scope it to those scenes. No `self`.

```
---
config:
 - game.toml          # exposes config.game.*
apis:
 - save               # framework service
 - OS                 # a Godot class, used as a bare global below
register:
 - on_start           # hooks this script handles (each must be a global function below)
 - on_update
---
function on_start()
  local n = save.get("launch_count", 0) + 1
  save.set("launch_count", n)
  print("launch #" .. n .. " on " .. OS.GetName() .. ", title=" .. config.game.title)
end

function on_update(dt)
  -- runs every frame
end
```

### Behavior script — `name.behavior.evt`
Behavior for a node, attached via a `.scene` file's `behaviors = [...]` list. Here `self`
IS that Godot node — no spawning. Uses node hooks (`on_attach`, `on_load`, …). **A node
holds N behaviors** (hooks fire in attachment order), and one behavior file can drive many
nodes, each with its own `params`. A behavior can also pull more behaviors/machines onto
its node via its own `behaviors:` / `machines:` frontmatter (composition — depth-first,
deduped). The old `name.node.evt` + `script =` pair still works as a **deprecated
single-behavior alias**; new code uses behaviors.

```
---
config:
 - player.toml
apis:
 - input
properties:
  position: [0, 0.5, 0]   # native engine state, applied at attach + on hot reload
register:
 - on_attach
 - on_update
---
function on_attach()
  self:set_meta("dmg", config.player.base_weapon_damage)
end

function on_update(dt)
  if input.is_down("right") then
    local p = self.position
    p.x = p.x + config.player.move_speed * dt
    self.position = p                                   -- assign back to persist
  end
end
```

`properties:` = static initial engine state (the scene's own keys win; unknown property =
load error). Convention: engine constants live in the signature or the scene, not the body.

#### Per-instance `params` (node-attached scripts)
`config.*` is shared by every node; **`params.*` is per-node**. The script declares typed
parameters in a `params:` block; the `.scene` file supplies values per attachment. A param
with no default is **required**; the scene cannot pass an undeclared param nor a wrong-typed
value — both error at load. Types: `number`/`string`/`bool`/`list`/`table`/`any`/**`dna`**.

```
--- enemy.behavior.evt
params:
  max_health: 100         # default 100 (number) — scene may override
  patrol_speed: number    # typed + REQUIRED — the scene must supply it
  dna: dna                # hand-authored identity: "0x" + EXACTLY 16 hex digits
register:
 - on_attach
---
function on_attach()
  self:set_meta("hp", params.max_health)
  self:set_meta("bravery", params.dna:trait(1))   -- slots 1..16, MSB first, each 0..15
end
```
```toml
[nodes.Enemy]
type = "Node3D"
behaviors = [{ script = "enemy.behavior.evt",
               params = { patrol_speed = 2.5, max_health = 60, dna = "0xA13F00C2D4E5B677" } }]
```

### State machine — `name.statemachine.evt`
Declarative FSM data attached to a node (`machines = [...]` in the scene, or a script's
`machines:` frontmatter). Frontmatter: `name:` (default = file stem), `states:` (required),
`initial:` (default = first). The body **returns the ordered transition list** — triggers
are `when` (guard fn, polled per physics tick, first match wins, one transition/tick),
`on = "event"` (fired via `self.fsm.<name>:fire("event")`), or `after = seconds`; optional
`run = fn(self, from, to)` (`do` is a Lua keyword). `from = "*"` is a wildcard.

```
---
name: stance
states: [idle, alert]
---
return {
  { from = "idle",  to = "alert", when = function(self) return self:get_meta("threat", false) end },
  { from = "alert", to = "idle",  after = 3.0 },
}
```

Behaviors on the node react through `self.fsm.stance` — `.state`, `:is(s)`, `:fire(evt)`,
`:on_exit(state, fn)`, and the sugar `self.fsm.stance.alert = fn(from)` (APPENDS an enter
listener). Re-subscribe in `on_load`: a behavior reload drops its stale listeners; a machine
reload keeps state + listeners.

### GAS-lite — `attributes:` / `abilities:` + `*.ability` / `*.effect` (TOML)
Node-attached scripts declare attribute **pools** with built-in stamina semantics (regen
after `regen_delay`; a spend that hits `min` EXHAUSTS the pool — tag `exhausted:<name>` —
until it regens to `recover`) and grant `*.ability` files (cooldown, `channeled` with
per-second cost, tags, `[[effects]]`). `*.effect` files mutate or modify attributes
(instant / while-active / timed / periodic). Read `self.attributes.<name>` (modifier-aware),
drive with `self.abilities:activate/deactivate/can_activate/has_tag/apply/on_ended`.
`.ability`/`.effect` files hot-reload live. Full grammar:
[reference/frontmatter-contract.md](reference/frontmatter-contract.md); runnable example:
[examples/sprint.behavior.evt](examples/sprint.behavior.evt).

### Scene file — `name.scene` (TOML)
A node tree. Table nesting **is** the tree. Node keys: `type` (required), `behaviors`,
`machines`, `params`, `meta`, `groups`, `unique`, `instance`, `connections` (+ the
deprecated `script`). See [reference/scene-grammar.md](reference/scene-grammar.md).

```
[nodes.Player]
type = "Node3D"
behaviors = ["player.behavior.evt"]      # attach behaviors (self = this node)
machines = ["stance.statemachine.evt"]   # attach state machines

[nodes.Player.Camera]                    # child of Player
type = "Camera3D"
position = [0, 2, -4]                    # property: array -> Vector3
```

## How members map (so you pick the right names)

- **Instance methods & properties are engine `snake_case`**: `node:get_node("X")`,
  `node.position`, `timer:emit_signal("timeout")`. (They route through the engine, which
  uses snake_case — `node:GetNode()` will NOT work.)
- **Constructors, enums and constants are C# `PascalCase`** on the declared class table:
  `Timer.new()`, `Key.Space`, `Timer.TimerProcessCallback.Idle`, `OS.GetName()`.
- **Vectors/colors/transforms** cross the boundary as tables: read a `Vector3` and you get
  `{x=,y=,z=}` (or `std.vec3`); assign a table/`std.vec3` back and it rebuilds the struct.

## Workflow for an agent using this skill

1. Decide the script kind (system / behavior / machine) and which scenes or nodes it
   belongs to.
2. Write the frontmatter **first** — declare every `api` (including each Godot class you
   construct or take enums from), every `config`/`asset`, and every hook you implement.
   Under-declaring is the #1 cause of failures ([common-mistakes.md](common-mistakes.md)).
3. Look up exact API names in the generated spec
   ([reference/api-discovery.md](reference/api-discovery.md)); for IDE autocomplete, load the
   LuaCATS files into lua-language-server.
4. Implement each declared hook as a global function (`function on_update(dt) … end`).
   Static engine state goes in `properties:`/the scene; rebuildable setup in `on_load`
   (pair with `on_unload`); once-only identity setup in `on_attach`.

## Reference

- [reference/frontmatter-contract.md](reference/frontmatter-contract.md) — every frontmatter key (incl. assets, properties, behaviors/machines, state machines, GAS-lite, dna).
- [reference/sandbox-rules.md](reference/sandbox-rules.md) — capabilities, api resolution, node surfaces, `require`/`returns`, what's withheld.
- [reference/lifecycle-hooks.md](reference/lifecycle-hooks.md) — every hook, args, and when it fires.
- [reference/scene-grammar.md](reference/scene-grammar.md) — `.scene` TOML rules.
- [reference/api-discovery.md](reference/api-discovery.md) — the generated, version-exact API spec.
- [common-mistakes.md](common-mistakes.md) — the sandbox errors and their fixes.
- [examples/](examples/) — runnable scripts to copy from (one consistent mini-game).
