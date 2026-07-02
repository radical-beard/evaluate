# Common mistakes (and fixes)

These are the failure modes the framework's own enforcement suite (98 cases) checks for.
Most are the sandbox doing its job: if you use something you didn't declare, the script
errors instead of silently misbehaving.

## 1. Reaching for the old ambient `godot.*` table

```lua
local t = godot.Timer.new()     -- ERROR: `godot` is nil — the ambient table is GONE
```

**Fix:** declare the class in `apis:` and use it as a bare global:

```
---
apis:
 - Timer
---
```
```lua
local t = Timer.new()
```

A `godot:`-prefixed `apis:` entry (`- godot:Node3D`) is also rejected, with a migration
hint — declare the class itself (`- Node3D`).

## 2. Using a capability you didn't declare

```lua
-- frontmatter declares no apis:
return input.is_down("right")   -- ERROR: `input` is nil — not declared in apis:
local n = Node3D.new()          -- ERROR: same rule — Godot classes are capabilities too
```

**Fix:** add it to frontmatter:

```
---
apis:
 - input
 - Node3D
---
```

Everything is declared: framework services (`input`, `world`, `scene`, `save`, `sql`),
host-registered apis, and Godot classes/enums. Only `std` is ambient. A typo *inside*
`apis:` (an unknown name) errors at load — you'll never get a silent `nil` from a
misspelled declaration. Note the flip side: **instances** (from `self`, `get_node`, signal
args, return values) expose members without any declaration — declaring gates the class
table, not the objects.

## 3. Loading resources imperatively

```
---
apis:
 - ResourceLoader     # ERROR at load: not a script capability
---
```

**Fix:** `ResourceLoader`, `ResourceSaver`, `FileAccess`, and `DirAccess` are blocked.
Assets come from the frontmatter `assets:` map (persistence from `save`/`sql`):

```
---
assets:
  rig: "models/hero.fbx"     # eagerly loaded; missing file = load error
---
-- assets.rig is the loaded resource, hot-reload included
```

The pre-0.10 bare-list form (`assets:` as a list of paths) is rejected too — every entry
needs a binding name (`name: "path"`).

## 4. Declaring a `returns` property without its accessors

```
---
returns:
 - position: get set vec3
---
local M = {}
return M                 -- ERROR at require time: no get_position / set_position exposed
```

**Fix:** expose the accessor functions the contract names:

```lua
local M, pos = {}, std.vec3(0,0,0)
function M.get_position() return pos end
function M.set_position(v) pos = v end
return M
```

Read-only? Declare `- position: get vec3` and expose only `get_position` (the setter stays
hidden from callers).

## 5. PascalCase on an instance method

```lua
local n = Node3D.new()   -- (Node3D declared in apis:)
n:GetChildCount()        -- does nothing useful: instance members are engine snake_case
```

**Fix:** `n:get_child_count()`. (Constructors/enums/statics on the declared class table
*are* PascalCase: `Node3D.new()`, `OS.GetName()`, `Key.Space` — see api-discovery.md.)

## 6. Mutating a struct in place without assigning it back

```lua
self.position.x = self.position.x + 1     -- reads a COPY; the node doesn't move
```

**Fix:** read, mutate, assign back:

```lua
local p = self.position
p.x = p.x + 1
self.position = p
```

## 7. A `register:` hook with no function (or vice versa)

A name in `register:` with no matching global function is silently never called; a function
not listed in `register:` is also never wired. Keep them in sync — every hook you implement
must be both `register:`-ed and defined as `function <name>(...) end`.

## 8. Scene-file slip-ups

- **Vector property written as a table:** `position = {x=0,y=1,z=0}` is read as a *child
  node* (and then errors: "no type"). Use an array: `position = [0, 1, 0]`.
- **Reserved characters in a node name:** `.  :  @  /  %  "` are rejected — Godot would
  rewrite the name and break `scene.find` path lookup.
- **Missing `type`:** every node needs `type = "SomeGodotClass"`.
- **Wrong extension in an attachment list:** `behaviors = ["m.evt"]` errors — a behaviors
  entry must be a `*.behavior.evt` (or legacy `*.node.evt`); `machines` entries must be
  `*.statemachine.evt`.

## 9. `params` mismatches between a behavior and its scene

`params` is a contract, like everything else in the signature — so the loader rejects:

- **A scene param the script never declared.** `params = { hp = 9, bogis = 1 }` against a
  `params:` block with no `bogis` errors (typo or undeclared). Declare it under `params:`, or
  remove it from the scene.
- **A required param the scene omits.** A `params:` entry given only a type (`speed: number`)
  is **required**; if the scene supplies no `speed`, it errors. Give it a default
  (`speed: number = 5`) or supply it in the scene.
- **A wrong-typed value.** `max_health: number` but the scene passes `max_health = "lots"`
  errors. Match the declared type (`number`/`string`/`bool`/`list`/`table`/`any`/`dna`).
- **A malformed `dna` value.** `dna: dna` accepts exactly `"0x"` + 16 hex digits
  (`"0xA13F00C2D4E5B677"`). Too short, no `0x`, non-hex digits, or a number all error —
  and the framework never generates one; author it by hand.

Also: `params:` is **node-attached scripts only** — declaring it on a system `.evt` (which
has no node instance) errors. And `params.*` is *per-instance*; for values shared by every
node use `config` (a TOML file) instead.

## 10. Putting static engine state in the body (or breaking `properties:` rules)

`self.position = std.vec3(0, 0.5, 0)` in `on_attach` works, but the convention is
frontmatter `properties:` (or the scene) for static initial engine state — it re-applies on
hot reload, so you can tune it live. Two hard errors to know: `properties:` on a **system**
script, and a **property name the node's class doesn't have** (`bogus_prop: 1`). And don't
fight the scene: a key the scene sets on the node always wins over the script's
`properties:`.

## 11. State-machine slip-ups

- **`do = fn`** — `do` is a Lua keyword; the transition action key is **`run`**
  (`run = function(self, from, to) ... end`).
- **Two triggers on one transition** (`after = 1, on = "x"`) errors — exactly one of
  `when` / `on` / `after`.
- **A transition naming an unknown state** (not in `states:`) errors, as does a machine
  with **no `states:`** at all.
- **`register:` on a machine** errors — machines have no hooks; they are polled data.
  React from a behavior via `self.fsm.<name>`.
- **Subscribing listeners outside `on_load`.** A behavior hot reload drops that behavior's
  stale fsm listeners, then re-fires `on_load` — so subscribe there (or in `on_attach` for
  once-only) and reloads never double-fire. A **machine** reload keeps state *and*
  listeners; only the machine's transitions refresh.

## 12. GAS slip-ups

- **Unknown keys are load errors:** an `.ability` with `bogus = 1`, an attribute spec key
  outside `base/min/max/regen/regen_delay/recover`, or an `.effect` targeting a field that
  doesn't exist (`attribute = "hp.bogus"`) all fail at load.
- **`attributes:`/`abilities:` on a system script** errors — pools live on nodes.
- **`abilities:apply` on a node with no attributes** (no behavior declared any) errors.
- **Assigning `self.attributes.x = 0` expecting exhaustion:** direct assignment is a hard
  drain — it clamps but does **not** exhaust. Only *spends* (ability costs) trigger the
  `exhausted:<name>` lockout.

## 13. Spawning from a behavior

A `*.behavior.evt` acts on `self`; it should not create the world around it. Create nodes
from a **system** script (`world:add_child(...)` / `scene.add(...)`) or declare them in a
`.scene` file.
