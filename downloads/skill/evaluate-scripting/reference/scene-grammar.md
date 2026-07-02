# Scene file grammar (`.scene`, TOML)

A `.scene` file is TOML that describes a Godot node tree. **Table nesting is the tree.** The
runtime instantiates it; the optional editor addon can round-trip it to/from a native scene
(including `behaviors` / `machines`, via `SceneWriter`).

```toml
# optional, only meaningful in the reserved global.scene manifest:
start_scene = "menu"
description = "free-text scene docs, kept as data (survives editor round-trips)"

[nodes.Player]
type = "Node3D"                                  # required: the Godot class to instantiate
behaviors = ["player.behavior.evt"]              # attach behavior scripts (self = this node)
machines = ["stance.statemachine.evt"]           # attach state machines (self.fsm.stance)

[nodes.Player.Camera]      # a sub-table => a CHILD node (Camera under Player)
type = "Camera3D"
position = [0, 2, -4]      # any other key => an engine property on the node
```

## Rules

- **A node's name is its table key.** `[nodes.Player.Camera]` names a node `Camera`, child
  of `Player`. Names may not contain `.  :  @  /  %  "` (Godot would rewrite them and break
  path lookup) — the parser rejects them.
- **Every node needs a `type`** (a Godot class name, e.g. `"Node3D"`, `"Area3D"`).
- **A sub-table is a child node** — *unless* it is tagged `_type` (see below).
- **Any other key is an engine property**, set on the instantiated node. Values marshal like
  Lua↔engine: `position = [0, 1, 2]` becomes a `Vector3`. **Vector/struct properties must be
  arrays `[x, y, z]`, not tables** (a bare table would be read as a child node). A property
  the scene sets **wins** over the same key in an attached script's `properties:` block.

## Reserved keys

| Key | Form | Effect |
|-----|------|--------|
| `type` | string | The Godot class to instantiate (**required**). |
| `behaviors` | array | Attach `*.behavior.evt` scripts, in order. Each entry is a path string or an inline table `{ script = "path", params = { … } }` for per-attachment params. A node holds N behaviors; one behavior file can drive many nodes. |
| `machines` | array | Attach `*.statemachine.evt` machines — same entry forms as `behaviors`. Reached as `self.fsm.<name>`. |
| `script` | string | **Legacy single-behavior alias** (deprecated): attach one `*.node.evt`. Equivalent to the first `behaviors` entry; new scenes use `behaviors`. |
| `params` | table | `params = { k = v }` → per-instance values for the `script =` behavior, read via its `params` global. (For `behaviors` entries, put `params` inside the entry table.) Validated against the script's declared `params:` (types, defaults, required, dna format). |
| `meta` | table | `meta = { k = v }` → `node.set_meta(k, v)` for each. |
| `groups` | array | `groups = ["enemies"]` → `node.add_to_group(...)`. |
| `unique` | bool | `unique = true` → unique name in owner (`%Name` lookups). |
| `instance` | string | Instance another scene; its roots become children here. |
| `connections` | array of tables | Declarative signal wiring: `{ signal = "...", to = "Path", method = "..." }` connects this node's signal to a built-in method on the node at `to`. (Lua handlers connect in code via `obj:connect(...)`.) |

Because these keys are reserved, a node can't be named `behaviors`, `machines`, `params`,
`meta`, `groups`, `instance`, `unique`, or `connections`.

```toml
# behaviors/machines, spelled out:
[nodes.Sentry]
type = "Node3D"
behaviors = [
  "patrol.behavior.evt",                                          # plain path
  { script = "sentry.behavior.evt", params = { dna = "0xA13F00C2D4E5B677" } },  # with params
]
machines = ["stance.statemachine.evt"]
```

## Inline resources & composite structs — `_type`

A sub-table tagged with `_type` is a **property**, not a child node — either an inline
resource or a builtin struct:

```toml
[nodes.Crate]
type = "MeshInstance3D"
mesh = { _type = "BoxMesh", size = [1, 1, 1] }                       # inline resource
custom_aabb = { _type = "Aabb", position = [1,2,3], size = [4,5,6] } # composite struct
```

Use `_type` for composite structs (`Transform3D`, `Rect2`, `Basis`, `Aabb`, `Projection`,
…). All-scalar structs (`Vector3`, `Color`, …) are just arrays: `position = [x, y, z]`.

A **quoted key** carries a `/` for Control theme overrides:
`"theme_override_colors/font_color" = [1, 0, 0, 1]`.

## The two layers

- **`global.scene`** (reserved manifest) — nodes that **persist across every scene switch**
  (e.g. the player), plus `start_scene`. Loaded once into the persistent Global root.
- **`*.scene`** (any other) — a **swappable** scene. Entering it builds its tree fresh under
  a per-scene container that is **freed wholesale** when you leave (via `scene.change`).
