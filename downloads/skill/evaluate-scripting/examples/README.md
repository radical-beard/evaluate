# Examples

Real, runnable EvaLuate scripts (lifted verbatim from the framework's own demo and test
suite). Copy and adapt them.

| File | Kind | Shows |
|------|------|-------|
| `system-global.evt` | system `.evt` (global) | `on_start`/`on_update`, `config`, `save` persistence, `godot.Timer` + signals, `std.vec3`↔`Vector3`, a `godot.OS.GetName()` static call. |
| `system-scene.evt` | system `.evt` (scene-scoped) | `scenes:` scoping, `on_enter`/`on_exit`/`on_update`, `scene.current()` / `scene.change()`. |
| `node.node.evt` | node `*.node.evt` | `self`-bound behavior, `on_attach`/`on_update`, `input.is_down`, `config.player.*`, the read-mutate-assign-back struct pattern. |
| `scene-global.scene` | `global.scene` manifest | persistent nodes, `start_scene`, attaching a node script, child nesting, a `Vector3` property as `[x,y,z]`. |
| `scene-swappable.scene` | `*.scene` | a swappable scene's node tree (freed wholesale on leave). |
| `config.toml` / `game-config.toml` | TOML config | the `[section]` → `config.section.*` shape declared via `config:`. |
| `module-returns.evt` | `require` module | the `returns:` contract — a read-only `get_hp` accessor. |
| `metatable-oop.evt` | module | idiomatic Lua OOP in the sandbox: `setmetatable`, `__index` inheritance, `raw*`. |
| `sql.evt` | system using `sql` | `sql.exec`/`exec_async`/`flush`/`query`, positional `@p1` params. |

The `.evt` files keep their frontmatter; the `.scene`/`.toml` files are plain TOML. None of
these need editing to run under EvaLuate — they are the same scripts the framework ships and
tests.
