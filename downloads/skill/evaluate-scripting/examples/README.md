# Examples

Real, runnable EvaLuate scripts — the system/module files are lifted from the framework's
own demo, and the behavior/machine/GAS files mirror its enforcement-suite cases. Copy and
adapt them. They form one small, consistent game: a persistent Player (two behaviors), a
swappable level with a Sentry (behavior + state machine + dna) and a shader-tinted Crate.

| File | Kind | Shows |
|------|------|-------|
| `system-global.evt` | system `.evt` (global) | declared apis — the framework `save` + the `OS`/`Timer`/`Node3D` class modules as bare globals; frontmatter `require:`; `config`; signals; `std.vec3`↔`Vector3`. |
| `lib/mathx.evt` | `require` module | the module `system-global.evt` binds via `require:` — its `returns` contract hides a private upvalue. |
| `system-scene.evt` | system `.evt` (scene-scoped) | `scenes:` scoping, `on_enter`/`on_exit`/`on_update`, `scene.current()` / `scene.change()`. |
| `player.behavior.evt` | behavior `*.behavior.evt` | `self`-bound behavior, frontmatter `properties:`, `config.player.*`, `input.is_down`, reading a sibling behavior's attribute, the read-mutate-assign-back struct pattern. |
| `sprint.behavior.evt` | behavior (GAS-lite) | `attributes:` (stamina with regen/exhaust/recover + a speed pool), `abilities:`, activate/deactivate a channel, `on_ended`. |
| `sprint.ability` | `*.ability` (TOML) | a channeled ability: per-second cost, `grant_tags`, an `[[effects]]` block. |
| `haste.effect` | `*.effect` (TOML) | a while-active (`duration = -1`) `add` modifier on the speed attribute. |
| `stance.statemachine.evt` | machine `*.statemachine.evt` | `name:`/`states:`/`initial:`, the returned transition list — `when` guard, `on` event with `run`, `after` timer, `from = "*"`. |
| `sentry.behavior.evt` | behavior + fsm + dna | the `self.fsm.<name>` surface (`:is`/`:fire`/enter-listener sugar/`:on_exit`), re-subscribing in `on_load`, and a `dna` param (`:trait(i)` / `:hex()`). |
| `tint.behavior.evt` | behavior + assets | the `assets:` map (eager load, ambient `assets` table), a `.gdshader` that hot-reloads in place, `on_load`/`on_unload` pairing. |
| `tint.gdshader` | shader asset | the shader `tint.behavior.evt` declares — shaders live in their own files. |
| `scene-global.scene` | `global.scene` manifest | persistent nodes, `start_scene`, a `behaviors = [...]` list (two behaviors, order matters), child nesting. |
| `scene-swappable.scene` | `*.scene` | a swappable tree: `behaviors` with per-attachment `params` (incl. a hand-authored dna hash), `machines`, an inline `_type` resource. |
| `config.toml` / `game-config.toml` | TOML config | the `[section]` → `config.section.*` shape declared via `config:`. |
| `module-returns.evt` | `require` module | the `returns:` contract — a read-only `get_hp` accessor. |
| `metatable-oop.evt` | module | idiomatic Lua OOP in the sandbox: `setmetatable`, `__index` inheritance, `raw*`. |
| `sql.evt` | system using `sql` | `sql.exec`/`exec_async`/`flush`/`query`, positional `@p1` params. |

The `.evt` files keep their frontmatter; the `.scene`/`.toml`/`.ability`/`.effect` files are
plain TOML. None of these need editing to run under EvaLuate.
