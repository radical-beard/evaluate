# Lifecycle hooks

A hook is a global function in the script body whose name you also list in `register:`.
The runtime calls it on the matching Godot lifecycle event. A hook not listed in `register:`
is never called; a name in `register:` with no matching function is simply not wired.

> The hook **names** below are authoritative (they come from the runtime). The exact set is
> also in the generated spec (`spec/evaluate-api.json` → `hooks`).

## System hooks — `name.evt` (no `self`)

| Hook | Args | Fires |
|------|------|-------|
| `on_start` | — | Once, after the global layer loads. **Global systems only.** |
| `on_load` | — | After `on_start`, and again on every hot reload. Put rebuildable setup here (make it idempotent). |
| `on_unload` | — | Before a reload tears the previous setup down. |
| `on_enter` | — | Each time a scope's scene becomes active (scene-scoped systems). |
| `on_exit` | — | Each time that scene is left (its nodes are about to be freed). |
| `on_update` | `dt` | Every frame while active. `dt` = seconds since last frame. |
| `on_physics_update` | `delta` | Every physics tick while active. |
| `on_input` | `event` | On input events (an `InputEvent` node). |
| `on_focus_in` / `on_focus_out` | — | App window gains / loses focus. |
| `on_pause` / `on_resume` | — | Scene tree paused / resumed. |
| `on_quit` | — | Once, as the tree tears down (good place to `sql.flush()` / final `save`). |

**Global vs scene-scoped:** a system with no `scenes:` is global — its hooks run in every
scene. With `scenes: [menu, level1]` its hooks run only while one of those is active. The
same hook (e.g. `on_update`) can be registered by a global system *and* by each scene's
system without clashing — a hook is registered once **per scene**.

## Behavior hooks — `name.behavior.evt` / `name.node.evt` (`self` bound)

`self` is the Godot node the behavior is attached to (via a `.scene` file's
`behaviors = [...]` list, another script's `behaviors:` frontmatter, or the legacy
`script =` key). A node holds **N behaviors**; each gets its own sandbox and its own hook
closures. **Hooks fire in attachment order** — `script =` first, then the `behaviors` list
in order, then frontmatter-composed behaviors (depth-first, deduped per node).

| Hook | Args | Fires |
|------|------|-------|
| `on_attach` | — | Once, when the node + this behavior first enter the tree. Never again. Frontmatter `properties:` are already applied. |
| `on_load` | — | Every (re)load — right after `on_attach` on first load, and on each hot reload of the script, a declared `config` TOML, or a declared (non-shader) `asset`. Make it idempotent. **Re-subscribe fsm listeners here** (a behavior reload drops its stale listeners). |
| `on_unload` | — | Before a reload tears the behavior's setup down. |
| `on_update` | `dt` | Every frame while the node is alive. |
| `on_physics_update` | `delta` | Every physics tick while alive. |
| `on_input` | `event` | On input events. |
| `on_exit` | — | When the node's container is freed. |
| `on_quit` | — | As the tree tears down. |
| `on_focus_in` / `on_focus_out` | — | Window focus changes. |
| `on_pause` / `on_resume` | — | Tree paused / resumed. |

Behaviors never spawn — they act on `self`. To create nodes, do it from a **system**
script (`world:add_child(...)` / `scene.add(...)`) or declare them in a `.scene` file.

## State machines have no hooks

A `*.statemachine.evt` may not `register:` anything — it is data (states + transitions)
that the framework ticks. Its `when`-guards are polled every **physics tick** (declaration
order, first match wins, at most one transition per tick); `after`-timers count time in
state; `on`-events run when fired. React to it from behaviors via the `self.fsm.<name>`
surface (enter-listener sugar, `:on_exit`) — see
[frontmatter-contract.md](frontmatter-contract.md) → "State machines".
