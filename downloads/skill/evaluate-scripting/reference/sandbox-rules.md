# Sandbox rules

Each script body runs in a fresh environment that holds **only**:

1. the language primitives (always),
2. `std` (always — the one ambient library),
3. `self` (node-attached scripts only),
4. `params` (node-attached scripts with a `params:` block),
5. `config` built from the declared `config:` files,
6. `assets` built from the declared `assets:` map,
7. **each capability listed in `apis:`, injected as its own bare global** — framework
   services, host-registered C# apis, and Godot classes/enums alike,
8. `require`, plus any locals bound by `require:` frontmatter.

Nothing else. There is **no ambient `godot.*` table** — the Godot library is reached module
by module, through `apis:`. The standard Lua `os`/`io` libraries exist on the shared VM but
are never copied in, so a script cannot reach them. `pcall` is withheld on purpose — errors
should surface, not be swallowed.

## Always available (no declaration)

- **Language primitives:** `pairs ipairs next type tostring tonumber error assert select
  string math table setmetatable getmetatable rawget rawset rawequal rawlen print`.
  The metatable toolkit is included, so idiomatic Lua OOP works (`setmetatable` + `__index`,
  inheritance, method dispatch).
- **`std.*`** — pure data types, no engine/IO reach: `std.vec3`, `std.vec2`, `std.color`,
  `std.vector`, `std.linked_list`.

## Declared capabilities (`apis:`)

An `apis:` entry resolves by name — precedence **framework service → host extension →
Godot class/enum** — and an unknown name is a **load error**, not a silent `nil`.

**Framework services:**

| api | What it is |
|-----|------------|
| `actions` | mapped input — `actions.<Scenario>.<Action>`: subscribe to press/release/tap/held, poll `.down`/`.value`/`.vector`. See "The input model" below. |
| `controller` | the native PlayerController — scenario switching, possession, rebinding, rumble, text capture. See "The input model" below. |
| `store` | global **session** state — survives every scene switch, never touches disk — `store.set/get/has/delete/keys/subscribe`. See below. |
| `world` | the persistent global-root **Node** (not a table) — `world:add_child(node)`; survives scene switches. |
| `scene` | `scene.change(name[, ctx])`, `scene.push/pop/stack` (a scene **stack**), `scene.context()`, `scene.current()`, `scene.find(path)`, `scene.add(node)`, `scene.list()` (every switchable scene name, manifest excluded). Stack + context semantics: [scene-grammar.md](scene-grammar.md) → "Scenarios, the player, and transitions". |
| `save` | SQLite-backed key/value persistence in `user://` — `save.set/get/delete`. |
| `sql` | full SQL — `sql.exec/exec_async/query/query_row/transaction/flush/snapshot`. |

**Host extensions:** C# apis the game registered with `runtime.RegisterApi("name", impl)`
(before the runtime entered the tree). Declared like any other api and called dot-style:
`apis: [combat_native]` → `combat_native.SweepArc(...)`.

**Godot classes & enums:** any engine class or enum name — `apis: [OS, Node3D,
Timer]` → `OS.GetName()`, `Node3D.new()`,
`Timer.TimerProcessCallback.Idle`.

Two boundaries to keep straight:

- **Declaration gates the class *table* only.** Instances that reach the script another way —
  `self`, `get_node(...)`, a signal argument, any call's return value — expose their members
  regardless. You declare a class to *name* it (constructor, statics, enums, constants), not
  to touch its instances.
- **Blocked from declaration:** `ResourceLoader`, `ResourceSaver`, `FileAccess`, `DirAccess`
  — assets come from frontmatter `assets:` (see
  [frontmatter-contract.md](frontmatter-contract.md)), persistence from `save`/`sql` — and
  the raw input classes: `Input`, `InputMap`, `Key`, `KeyModifierMask`, `JoyButton`,
  `JoyAxis`, `MouseButton`, `MouseButtonMask`, plus every `InputEvent*` class
  (prefix-blocked). The error points to the `actions` api — see "The input model" below.

Use an undeclared name in the body and the lookup yields `nil` → a runtime error (see
[common-mistakes.md](../common-mistakes.md)). A typo *inside* `apis:` errors at load.

## The input model — `actions` + `controller`

Raw input never reaches scripts: there is no input hook, and the raw input classes are
blocked from `apis:` (list above). Instead a native **PlayerController** node — implicitly
added to the global layer — reads the hardware and resolves it through the manifest's
controls TOML (`controls = "path/to/controls.toml"` in `global.scene`; scripts-relative,
hot-reloaded), which maps physical bindings to named **actions**, grouped by **scenario**.
TOML grammar and scenario semantics: [scene-grammar.md](scene-grammar.md).

**`actions`** exposes `actions.<Scenario>.<Action>` — an unknown scenario or action name
errors listing what exists. Per action:

```lua
-- Subscribe in on_load: a hot reload drops the subscribing script's stale closures and
-- re-fires on_load (scene-layer subscriptions also die with their scene). Returns a
-- handle table with cancel().
local h = actions.Gameplay.Jump:subscribe{ on = "press", run = function() ... end }
actions.Gameplay.Interact:subscribe{ on = "tap",  after = 0.15, run = ... }  -- max hold time
actions.Gameplay.Charge:subscribe{ on = "held", after = 0.4,  run = ... }    -- min hold time
-- `after` defaults to 0.2 s. The callback key is `run`, NOT `do` (a Lua keyword —
-- the same reason statemachine transitions use `run`).

-- Live polled reads:
actions.Gameplay.Jump.down     -- bool
actions.Gameplay.Block.value   -- 0..1 (analog axis; "down" past axis_threshold)
actions.Gameplay.Move.vector   -- {x, y}; +y = up/forward; stick deadzone applied radially
```

Action events fire once per **physics tick**, before any `on_physics_update` runs.

**`controller`** is the PlayerController's surface:

```lua
controller.scenario("Menu")        -- set the active scenario; controller.scenario() reads it
controller.possess(node)           -- route control to a node; controller.possessed() / .release()
controller.rebind("Gameplay", "Key_e", "Interact")  -- action nil = unbind; persisted in the
                                   -- save DB (control_overrides), applied over the TOML
controller.overrides()             -- the persisted override set
controller.reset_overrides()
controller.rumble(0.8, 0.3)        -- strength 0..1, seconds
controller.capture_text(function(kind, char) ... end)
                                   -- kind = "char" (with the character) | "backspace";
                                   -- while capturing, printable keys stop firing mapped
                                   -- actions; capture_text(nil) stops
controller.layout("dvorak")        -- select a declared keyboard layout (persisted in the
                                   -- save DB, remaps live); controller.layout() reads it,
                                   -- controller.layouts() lists the declared ones
controller.joy_name()
```

Subscription callbacks run **under the registering script's context**: anything a callback
itself registers (another subscription, a `capture_text`) is owned by that script and its
scene layer, so it is cleaned up on hot reload / scene teardown with everything else.

Exactly **one scenario is active** at a time (plus the reserved `Always` section, active in
every scenario). Switching scenarios fires synthetic **releases** for held actions, and a
binding physically held across the switch is suppressed until first released.

## `store` — global session state

`store` is key/value state scoped to the **session**: it survives every scene switch but is
**not persisted to disk** (persistence is `save`/`sql`).

```lua
store.set("run.gold", 120)
store.get("run.gold", 0)                 -- default when absent
store.has("run.gold")                    -- bool
store.delete("run.gold")
store.keys("run.")                       -- every key under the prefix
store.subscribe("run.gold", function(key, new, old) ... end)   -- exact key
store.subscribe("run.",     function(key, new, old) ... end)   -- trailing "." = prefix
-- subscribe returns a handle table with cancel()
```

## Node surfaces (not sandbox globals)

`self.fsm`, `self.attributes`, and `self.abilities` are **per-node surfaces**, attached by
the framework when a machine / `attributes:` / `abilities:` declaration lands on the node.
They ride on the node, so *every* behavior on that node (and any script holding the node)
sees the same machines and pools — no declaration in the reader's own frontmatter needed.

## Withheld on purpose

`os`, `io`, `pcall` — absent from every sandbox. Don't use them. (For persistence use
`save`/`sql`; for time declare the `Time` class; let errors propagate rather than
`pcall`-wrapping.)

## `require` and the `returns` contract

`require("module")` runs `module.evt` once (cached) and returns a handle **narrowed** to its
`returns:` declaration:

- a plain `- name` exposes `module.name` verbatim;
- a `- name: get set <type>` exposes `get_name` / `set_name` accessor functions;
- `get`-only hides the setter;
- a declared member the module doesn't actually expose is a hard error at `require` time.

This is the C# ↔ Lua and module ↔ module boundary: callers can reach exactly the declared
surface, nothing more.

You can also declare the dependency in frontmatter with `require:` (see
[frontmatter-contract.md](frontmatter-contract.md)) — `require: { base: "lib/base.evt" }`
binds `base` as a sandbox local with no `local base = require(...)` line. It resolves the
same narrowed handle. Two guarantees the frontmatter form makes explicit: a **require cycle**
(direct or transitive, including self-require) is rejected with a clear error rather than
overflowing the stack, and editing a required module **hot-reloads every consumer** that
requires it (transitively).
