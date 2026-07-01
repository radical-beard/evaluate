# Sandbox rules

Each script body runs in a fresh environment that holds **only**:

1. the language primitives (always),
2. `std` and `godot` (always — ambient),
3. `self` (node scripts only),
4. `config` built from the declared `config:` files,
5. each capability listed in `apis:`,
6. `require`, plus any locals bound by `require:` frontmatter.

Nothing else. The standard Lua `os`/`io` libraries exist on the shared VM but are **never
copied in**, so a script cannot reach them. `pcall` is withheld on purpose — errors should
surface, not be swallowed.

## Always available (no declaration)

- **Language primitives:** `pairs ipairs next type tostring tonumber error assert select
  string math table setmetatable getmetatable rawget rawset rawequal rawlen print`.
  The metatable toolkit is included, so idiomatic Lua OOP works (`setmetatable` + `__index`,
  inheritance, method dispatch).
- **`std.*`** — pure data types, no engine/IO reach: `std.vec3`, `std.vec2`, `std.color`,
  `std.vector`, `std.linked_list`.
- **`godot.*`** — the whole engine type system, resolved on first use.

## Declared capabilities (`apis:`)

| api | What it is |
|-----|------------|
| `input` | `input.is_down(key)` — is a (host-mapped) key currently held. |
| `world` | the persistent global-root **Node** (not a table) — `world:add_child(node)`; survives scene switches. |
| `scene` | `scene.change(name)`, `scene.current()`, `scene.find(path)`, `scene.add(node)`. |
| `save` | SQLite-backed key/value persistence in `user://` — `save.set/get/delete`. |
| `sql` | full SQL — `sql.exec/exec_async/query/query_row/transaction/flush/snapshot`. |

Use one without declaring it and the lookup yields `nil` → a runtime error (see
[common-mistakes.md](../common-mistakes.md)).

## Withheld on purpose

`os`, `io`, `pcall` — absent from every sandbox. Don't use them. (For persistence use
`save`/`sql`; let errors propagate rather than `pcall`-wrapping.)

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
