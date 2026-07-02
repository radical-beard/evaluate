# Frontmatter contract

Every script — `.evt` (system), `.behavior.evt` / `.node.evt` (behavior),
`.statemachine.evt` (machine) — may begin with a `---`-delimited YAML block. It is the
**signature**: it declares what the script needs and exposes. Everything below the closing
`---` is the Lua body (its line numbers are preserved for error messages).

A script with no leading `---` is all body — valid, but it gets no capabilities and
registers no hooks.

```
---
config:
 - game.toml
apis:
 - save
 - actions
 - Node3D
assets:
  tint: "shaders/tint.gdshader"
register:
 - on_start
 - on_update
scenes:
 - level1
returns:
 - spawn
 - hp: get double
---
-- lua body here
```

## The keys

| Key | Value | Meaning |
|-----|-------|---------|
| `config` | list of TOML files | Each file becomes `config.<section>.<key>`. `player.toml` with `[player]` → `config.player.*`. Read live (hot-reloads). |
| `apis` | list of capability names | **Everything** the body reaches beyond `std`/primitives — framework services, host-registered C# apis, and Godot classes/enums alike. Each is injected as its own bare global. See below. |
| `register` | list of hook names | Lifecycle hooks this script handles. Each name must have a matching global `function <name>(...)` in the body. See [lifecycle-hooks.md](lifecycle-hooks.md). Not allowed on machines. |
| `returns` | list of member specs | For a script loaded via `require` — the contract of the returned module. See below. |
| `params` | map of name → spec | **Node-attached scripts only.** Typed, per-instance values the `.scene` file supplies (`params = {..}`), read via the `params` global. Includes the `dna` type. See below. |
| `require` | name → module path | Binds modules to sandbox locals up front, so the body uses them with no `local x = require(...)` line. See below. |
| `scenes` | list of scene names | **System scripts only.** Restricts the script's hooks to those scenes. Omit ⇒ **global** (runs in every scene). |
| `assets` | map of name → res-relative path | The **only** way a script gets an asset. Eagerly loaded, injected as the ambient `assets` table, hot-reloaded. See below. |
| `properties` | map of property → value | **Node-attached scripts only.** Native Godot properties applied to `self` at attach and re-applied on script hot reload. See below. |
| `behaviors` | list of behavior paths | Composition: more `*.behavior.evt` files this script pulls onto the **same node**. See below. |
| `machines` | list of machine paths | Composition: `*.statemachine.evt` files this script pulls onto the same node. |
| `attributes` | map of name → spec | **Node-attached scripts only.** GAS-lite attribute pools (stamina-style). See below. |
| `abilities` | list of `*.ability` files | GAS-lite abilities granted to the node at attach. See below. |
| `name` / `states` / `initial` | — | **Machines only** (`*.statemachine.evt`). See "State machines" below. |

`config` and `apis` accept either YAML list form (`- game.toml`) or omitted/empty.

## The `apis` grammar — every capability is a declared module

There is **no ambient `godot.*` table**. Each `apis:` entry is resolved by name — precedence
**framework service → host-registered extension → Godot class/enum** — and injected as its
own bare global table:

```
---
apis:
 - actions        # framework service (mapped input)
 - combat_native  # a host api the game registered via runtime.RegisterApi(...)
 - OS             # a Godot singleton-class: OS.GetName()
 - Node3D         # a Godot class: Node3D.new()
 - Timer          # classes carry their enums: Timer.TimerProcessCallback.Idle
---
local n = Node3D.new()
if actions.Gameplay.Jump.down and OS.GetName() ~= "Web" then ... end
```

Rules:

- **Declaration gates the class table only.** Instances that reach the script another way —
  `self`, `get_node(...)`, signal arguments, a value returned by any call — expose their
  members regardless of what is declared. Declaring `Node3D` is about *naming the class*
  (constructor, statics, enums, constants), not about touching Node3D instances.
- **An unknown name is a load error** (typo-proofing), not a silent `nil`: the message lists
  what a name can be (a framework api
  `actions`/`controller`/`store`/`world`/`scene`/`save`/`sql`, a registered
  host api, or a Godot class/enum).
- **`godot:`-prefixed entries error** with a migration hint — declare the class itself
  (`apis: [Node3D]`) and use it as a bare global.
- **Blocked from declaration:** `ResourceLoader`, `ResourceSaver`, `FileAccess`,
  `DirAccess` — assets come from frontmatter `assets:`; persistence from `save`/`sql`.
  Likewise the raw input classes — `Input`, `InputMap`, `Key`, `KeyModifierMask`,
  `JoyButton`, `JoyAxis`, `MouseButton`, `MouseButtonMask`, and every `InputEvent*` class
  (prefix-blocked) — input is mapped through the controls TOML and read via the `actions`
  api (the error says so). See [sandbox-rules.md](sandbox-rules.md) → "The input model".

## The `returns` grammar

`returns:` declares the public shape of a module that other scripts pull in with
`require("module")`. The runtime **narrows** the returned table to exactly these members —
anything undeclared is unreachable, and a declared member the module forgot to expose is an
error at `require` time.

Two forms per entry:

- **Plain member** — `- spawn` — the module must return a table with a `spawn` field
  (usually a function). Exposed as-is.
- **Property** — `- hp: get set double` — a get/set accessor pair. The module must expose
  `get_hp` (for `get`) and/or `set_hp` (for `set`). Read-only is just `get` (omit `set`),
  and then no setter is exposed.

```
---
returns:
 - hp: get double      -- read-only: module exposes get_hp(), callers cannot set
---
local M, hp = {}, 100
function M.get_hp() return hp end
return M
```

The access spec is `get`/`set` tokens plus a trailing type name (the type is documentation —
there is no Lua type system; the contract is structural).

## The `require` grammar

`require:` binds other modules to **sandbox locals**, so shared code is declared in the
signature instead of restated as `local x = require("…")` boilerplate atop the body. Each
binding resolves through the same custom `require` the body could call — the path is loaded,
run once (cached), and **narrowed to its `returns` contract** — so a `require:` binding is
exactly what `require(path)` would return, just bound to a name up front. Paths resolve
relative to the scripts root (the same string you'd pass to `require`).

Two equivalent forms — a list of single-key maps, or a map (like `params:`):

```
---
require:
 - base: "lib/control/controllable.evt"     # list form
 - locomotion: "player/locomotion.evt"
---
```
```
---
require:
  base: "lib/control/controllable.evt"       # map form — identical result
  locomotion: "player/locomotion.evt"
---
-- `base` and `locomotion` are ready to use; no `local base = require(...)` needed.
function on_attach() base.init(self); locomotion.walk(self, 2) end
```

Rules:

- A binding name may **not** shadow a reserved/declared sandbox name (`std`, `config`,
  `params`, `self`, `assets`, a declared `api`, a language global, or another binding) — a
  collision is an error at load time.
- A **require cycle** (A requires B requires A, directly or transitively — including a script
  requiring itself) is rejected with a clear error, not a stack overflow.
- Editing a required module **hot-reloads every consumer** that requires it (transitively):
  systems re-run their bodies and live behaviors refresh, rebinding the fresh module.
- Inline `require("path")` still works; `require:` is just the declared-up-front form.

## The `params` grammar

`params:` (node-attached scripts only) declares **per-instance** values supplied by the
`.scene` file that attaches the script — the per-node analogue of `config` (which is shared
across all nodes). It is a YAML **map** of `name → spec`. The scene supplies values with a
`params = {..}` table on the node (or on a `behaviors = [{ script = …, params = {..} }]`
attachment); the body reads the resolved set through the `params` global.

Each spec is one of:

- **`name: <default>`** — a bare scalar is the default; the type is inferred from it.
  `max_health: 100` (number), `faction: "neutral"` (string), `can_fly: false` (bool). A
  list/table value is a list/table default.
- **`name: <type>`** — a type token alone makes the param **required**: the scene *must*
  supply it. `patrol_speed: number`.
- **`name: '<type> = <default>'`** — both. `aggro_range: number = 8`.

Types: `number`, `string`, `bool`, `list`, `table`, `any` (`any` skips the type check), and
**`dna`** (see below).

The contract is enforced when the node is built:

- a scene value for a param the script **did not declare** is an error (you can't inject
  values past the signature);
- a value whose type **disagrees** with the declared type is an error;
- a **required** param (no default) the scene **omits** is an error.

```
--- turret.behavior.evt
params:
  range: number = 12      # typed, default 12
  team: "red"             # default "red"
  target_tag: string      # REQUIRED — the scene must supply it
register:
 - on_attach
---
function on_attach()
  print(params.team .. " turret, range " .. params.range .. ", targets " .. params.target_tag)
end
```
```toml
[nodes.Turret]
type = "Node3D"
behaviors = [{ script = "turret.behavior.evt", params = { target_tag = "enemy", range = 20 } }]
```

### The `dna` param type

`dna` is a 64-bit, **hand-authored** identity hash: `"0x"` + **exactly 16 hex digits** (the
framework never generates one — every trait slot is visibly chosen). Anything else — wrong
length, missing `0x`, non-hex digits, a non-string — is rejected at load.

```
---
params:
  dna: dna
---
```
```toml
behaviors = [{ script = "sentry.behavior.evt", params = { dna = "0xA13F00C2D4E5B677" } }]
```

The body reads it as an object: `params.dna:trait(i)` — slots 1..16, most-significant digit
first, each 0..15 — and `params.dna:hex()` (the normalized uppercase string). The same hash
always produces the same character.

## The `assets` grammar

`assets:` is a **map** of `name → res-relative path` (or a list of single-key maps) and is
the **only** way a script gets an engine asset — `ResourceLoader` & co. are blocked from
`apis:`. Every entry is **eagerly loaded at script load** (a missing file is a load error
naming the binding, not a runtime `nil`) and injected as the ambient **`assets`** table:

```
---
assets:
  tint: "shaders/tint.gdshader"    # assets.tint      -> a Shader
  rig:  "models/hero.fbx"          # assets.rig       -> the imported scene resource
  pack: "shaders/fx_*.gdshader"    # assets.pack.fx_a -> a glob binds a stem-keyed table
---
```

Hot reload is real:

- a **`.gdshader`** edit updates **in place** — the *same* `Shader` instance (even one a
  script captured at load) carries the new code, so every live `ShaderMaterial` recompiles
  for free. This is why shaders must live in their own `.gdshader` files, never inline.
- any **other resource** re-loads fresh (cache-replace), then every script that declares it
  re-runs `on_unload` → `on_load`, so `on_load` picks up the new instance.

## The `properties` grammar

`properties:` (node-attached scripts only) is a map of **native Godot properties** applied
to `self` when the node attaches — before `on_attach` runs — and **re-applied on script hot
reload** (edit the block, watch the node change). Values use the same grammar as `.scene`
properties: scalars, `[x, y, z]` arrays for vectors/colors, `_type` tables for composite
structs.

```
---
properties:
  position: [0, 0.5, 0]
  visible: true
---
```

Rules:

- **The scene wins.** A property key the scene itself sets on the node is never overwritten
  by the script's `properties:` — at attach or on reload.
- **An unknown property name is a load error** (it must exist on the node's class).
- Declaring `properties:` on a system script is an error.

Convention: static initial engine state belongs here (or in the scene), not in the body —
`on_attach` is for logic, not for `self.position = …` constants.

## `behaviors` / `machines` — frontmatter composition

A node-attached script may pull **more attachments onto its own node**:

```
--- creature.behavior.evt
behaviors:
 - lib/breathing.behavior.evt
 - lib/flocking.behavior.evt
machines:
 - lib/lifecycle.statemachine.evt
---
```

Attachments compose **depth-first** and are **deduped per node** (a behavior reached twice
attaches once). Hooks fire in attachment order. The scene's `behaviors = [...]` /
`machines = [...]` lists are the other attachment channel — see
[scene-grammar.md](scene-grammar.md). Composition is structural: editing these lists applies
on scene rebuild, not on hot reload.

## State machines — `*.statemachine.evt`

A machine file's frontmatter is its own small schema (no `register:` — a machine has no
hooks):

| Key | Meaning |
|-----|---------|
| `name` | The machine's name on the node: `self.fsm.<name>`. Defaults to the file stem. |
| `states` | **Required.** The full state list. A transition naming an unknown state is a load error. |
| `initial` | Starting state. Defaults to the first entry of `states`. |

The **body returns the ordered transition list**. Each transition has `from`, `to`, exactly
**one trigger**, and an optional action:

```lua
return {
  { from = "idle",  to = "alert",  when = function(self) return self:get_meta("threat", false) end },
  { from = "alert", to = "attack", on = "provoked", run = function(self, from, to) ... end },
  { from = "alert", to = "idle",   after = 3.0 },
  { from = "*",     to = "idle",   on = "calm" },       -- wildcard from-state
}
```

- **`when = fn(self)`** — a guard, polled every physics tick in declaration order; first
  match wins; at most one transition per tick.
- **`on = "event"`** — taken when a script calls `self.fsm.<name>:fire("event")`. Immediate;
  a fire from inside a listener (reentrant) defers one tick.
- **`after = seconds`** — taken after that long in the `from` state.
- **`run = fn(self, from, to)`** — optional action when the transition is taken (`do` is a
  Lua keyword, hence `run`).

Node surface (available to every behavior on the node):

```lua
self.fsm.stance.state                 -- current state name
self.fsm.stance:is("alert")           -- boolean
self.fsm.stance:fire("provoked")      -- returns true if a transition ran
self.fsm.stance:on_exit("attack", function(to) ... end)
self.fsm.stance.alert = function(from) ... end   -- sugar: APPENDS an enter listener
```

Hot reload: editing the **machine** refreshes its transitions but **keeps** the current
state and all listeners; reloading a **behavior** drops that behavior's stale listeners —
so re-subscribe in `on_load` (or `on_attach`) and they never double up.

Attach machines via the scene (`machines = [...]`) or a script's `machines:` frontmatter.

## `attributes` / `abilities` — GAS-lite

`attributes:` (node-attached only) declares per-node attribute **pools** with built-in
stamina semantics; `abilities:` lists `*.ability` files granted at attach:

```
---
attributes:
  stamina: { base: 100, min: 0, max: 100, regen: 20, regen_delay: 0.5, recover: 25 }
abilities:
 - sprint.ability
---
```

Attribute spec keys (all numeric; unknown keys are a load error): `base` (starting value),
`min`/`max` (clamp range; `min` defaults to 0, `max` to `max(base, min)`), `regen` (per
second), `regen_delay` (seconds after the last **spend** before regen resumes), `recover`
(defaults to 25% of the range above `min`). Draining to `min` **via a spend** EXHAUSTS the
attribute — the node holds the tag `exhausted:<name>` and the spending ability can't
re-activate — until it regens back up to `recover`.

**`*.ability` (TOML):** `cooldown` (seconds), `channeled` (true = stays active until
deactivated; the cost then drains **per second** instead of per activation),
`cost = { attribute, amount }`, `tags` / `block_tags` / `grant_tags` (grant_tags are held
while a channel is active), and `[[effects]]` blocks
(`effect = "path.effect"`, `target = "self"`).

**`*.effect` (TOML):** `attribute` (may be dotted to target a spec field — `"stamina.max"`,
`"stamina.regen"`, `"stamina.regen_delay"`, `"stamina.min"`, `"stamina.recover"`),
`op = "add" | "mul" | "set"`, `magnitude`, `duration` (`0` instant permanent mutation, `-1`
while-source-active modifier, `> 0` timed seconds), `period` (`> 0` = re-apply `magnitude`
every `period` seconds — dot/hot).

Lua surface (per-node — every behavior on the node shares it):

```lua
self.attributes.stamina               -- modifier-aware, clamped read
self.attributes.stamina = 0           -- assign: sets the stored value (clamped);
                                      -- a hard drain does NOT exhaust (only spends do)
self.attributes:max("stamina")        -- effective max
self.attributes:has("stamina")        -- declared?

self.abilities:grant("dash.ability")            -- runtime grant (attach grants are frontmatter)
self.abilities:activate("sprint")               -- -> bool (cost+cooldown+tags permitting)
self.abilities:deactivate("sprint")             -- end a channel
self.abilities:is_active("sprint")              -- channel running?
self.abilities:can_activate("sprint")           -- would activate succeed?
self.abilities:cooldown("sprint")               -- seconds remaining
self.abilities:has_tag("sprinting")             -- tag check (incl. exhausted:<attr>)
self.abilities:apply("heal.effect")             -- apply an effect to self
self.abilities:apply("burn.effect", other_node) -- ... or to another attribute-bearing node
self.abilities:on_ended("sprint", function(name, reason) end)  -- "deactivated" | "exhausted"
```

`.ability` / `.effect` files **hot-reload live** — the next activation uses the edited
values. Re-declaring an attribute on reload re-tunes its spec and keeps the current value.
