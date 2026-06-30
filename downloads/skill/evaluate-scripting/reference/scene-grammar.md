# Scene file grammar (`.scene`, TOML)

A `.scene` file is TOML that describes a Godot node tree. **Table nesting is the tree.** The
runtime instantiates it; the optional editor addon can round-trip it to/from a native scene.

```toml
# optional, only meaningful in the reserved global.scene manifest:
start_scene = "menu"
description = "free-text scene docs, kept as data (survives editor round-trips)"

[nodes.Player]
type = "Node3D"            # required: the Godot class to instantiate
script = "player.node.evt" # optional: attach a node script (self = this node)

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
  arrays `[x, y, z]`, not tables** (a bare table would be read as a child node).

## Reserved keys

| Key | Form | Effect |
|-----|------|--------|
| `type` | string | The Godot class to instantiate (**required**). |
| `script` | string | Attach a `.node.evt` node script. |
| `params` | table | `params = { k = v }` → per-instance values for the node script, read via its `params` global. Validated against the script's declared `params:` (types, defaults, required). |
| `meta` | table | `meta = { k = v }` → `node.set_meta(k, v)` for each. |
| `groups` | array | `groups = ["enemies"]` → `node.add_to_group(...)`. |
| `unique` | bool | `unique = true` → unique name in owner (`%Name` lookups). |
| `instance` | string | Instance another scene; its roots become children here. |
| `connections` | array of tables | Declarative signal wiring: `{ signal = "...", to = "Path", method = "..." }` connects this node's signal to a built-in method on the node at `to`. (Lua handlers connect in code via `obj:connect(...)`.) |

Because `params`/`meta`/`groups`/`instance`/`unique`/`connections` are reserved, a node
can't be named one of them.

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
