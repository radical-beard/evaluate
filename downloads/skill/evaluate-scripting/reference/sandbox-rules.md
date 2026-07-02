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
| `input` | `input.is_down(key)` — is a (host-mapped) key currently held. |
| `world` | the persistent global-root **Node** (not a table) — `world:add_child(node)`; survives scene switches. |
| `scene` | `scene.change(name)`, `scene.current()`, `scene.find(path)`, `scene.add(node)`, `scene.list()` (every switchable scene name, manifest excluded). |
| `save` | SQLite-backed key/value persistence in `user://` — `save.set/get/delete`. |
| `sql` | full SQL — `sql.exec/exec_async/query/query_row/transaction/flush/snapshot`. |

**Host extensions:** C# apis the game registered with `runtime.RegisterApi("name", impl)`
(before the runtime entered the tree). Declared like any other api and called dot-style:
`apis: [combat_native]` → `combat_native.SweepArc(...)`.

**Godot classes & enums:** any engine class or enum name — `apis: [Input, Node3D, Key,
Timer]` → `Input.GetJoyAxis(...)`, `Node3D.new()`, `Key.Space`,
`Timer.TimerProcessCallback.Idle`.

Two boundaries to keep straight:

- **Declaration gates the class *table* only.** Instances that reach the script another way —
  `self`, `get_node(...)`, a signal argument, any call's return value — expose their members
  regardless. You declare a class to *name* it (constructor, statics, enums, constants), not
  to touch its instances.
- **Blocked from declaration:** `ResourceLoader`, `ResourceSaver`, `FileAccess`, `DirAccess`.
  Assets come from frontmatter `assets:` (see
  [frontmatter-contract.md](frontmatter-contract.md)); persistence from `save`/`sql`.

Use an undeclared name in the body and the lookup yields `nil` → a runtime error (see
[common-mistakes.md](../common-mistakes.md)). A typo *inside* `apis:` errors at load.

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
