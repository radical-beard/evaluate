# Changelog

All notable changes to Evaluate are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While
the version is `0.x`, minor bumps may include breaking changes.

## [0.3.0] — 2026-06-18

Scenes: a two-tier model so one program can hold many scenes, each with its own
registered functions, plus declarative scene files and per-node behavior scripts.

### Added
- **Global / scene layers.** A persistent **global layer** (never cleared) and a
  swappable **scene layer** beneath it. `scene.change(name)` tears down the active
  scene's container wholesale and builds the next one; the global layer is
  untouched.
- **`global.scene.toml`** — reserved manifest declaring persistent nodes (e.g. the
  player) and the `start_scene` entered after startup.
- **`*.scene.toml`** — declarative scene files. `[[node]]` blocks carry `name`,
  `type`, `parent` (by name), `script`, and engine properties (e.g.
  `position = [x, y, z]`). Auto-discovered and hot-reloadable.
- **`*.node.evt`** — node-behavior scripts attached to a node via the scene file,
  with **`self`** bound to that node. No spawning in the script; the scene file
  owns structure.
- **`scenes:` frontmatter field** on system scripts — declares scene membership
  (empty = global, runs everywhere).
- **`scene` capability API** — `scene.change(name)`, `scene.current()`,
  `scene.find(name)`, `scene.add(node)` (parent a node under the active scene
  container so it is freed on exit).
- **New lifecycle hooks.** Node hooks (with `self`): `on_ready`, `on_update`,
  `on_physics_update`, `on_input`, `on_exit`. System scene hooks: `on_enter` /
  `on_exit`, fired on each scene activation/deactivation.
- **Hot reload for the new artifacts** — editing a `*.node.evt` refreshes its hook
  closures on every live instance; editing the active `*.scene.toml` rebuilds the
  scene in place.
- **TOML array-of-tables** (`[[name]]`) via `Toml.ParseDocument` (used by scene
  files; the flat `Toml.Parse` for config is unchanged).
- **Positional vector properties** — a list (`[x, y, z]`) assigned to a
  vector/color/quaternion property builds the struct, so scene files can write
  `position = [0, 1, 2]`.
- **`GodotBinder.Instantiate` / `SetProperty`** — construct nodes by type name and
  set engine properties from data, used to build scene trees.

### Changed
- **Breaking — hook registration is now once *per scene*, not once per project.**
  The same hook (e.g. `on_update`) can be registered in `menu`, `level1`, and
  globally, each by a different script. Two scripts still cannot claim the same
  hook within the same scene namespace.
- **Breaking — the `world` API now resolves to the persistent global root** (the
  requesting script's layer container), not the runtime node. Nodes added via
  `world` survive scene switches; for scene-local content use `scene.add(node)` or
  declare nodes in a `*.scene.toml`.
- `on_start` is restricted to global systems; `on_enter` / `on_exit` to
  scene-scoped systems.
- Enforcement suite grew from 7 to 12 tests (per-scene claims, cross-scene
  allowance, scene-switch gating, `self` binding, scene-file parsing).

### Removed
- The old conductor-style demo scripts (`game.evt`, `player.evt`,
  `enemy_factory.evt`) — replaced by the global/scene/node demo (`global.scene.toml`,
  `menu`/`level1` scenes, `player.node.evt`, `showcase.evt`).

### Notes
- The scene-switch verb is **`scene.change`**, not `goto` — `goto` is a reserved
  word in Lua 5.2, so `scene.goto(...)` is a syntax error.

## [0.2.0] — 2026-06-18

### Added
- Metatable builtins in the sandbox — `setmetatable`, `getmetatable`, `rawget`,
  `rawset`, `rawequal`, `rawlen` — so scripts can define classes the idiomatic Lua
  way (`setmetatable` + `__index`). These are pure language features with no
  engine/IO reach, so they are capability-free like `std`. `pcall` remains
  intentionally withheld (errors surface rather than being swallowed).

## [0.1.0] — 2026-06-16

Initial release — a data-driven, moddable game framework that runs inside Godot
4.6 / .NET, with gameplay authored in sandboxed Lua (`.evt`) scripts + TOML.

### Added
- **Frontmatter → signature.** YAML frontmatter (`config:`, `apis:`, `register:`,
  `returns:`, `assets:`) declares a capability-scoped contract; the Lua body runs
  verbatim with line numbers preserved.
- **Per-script sandbox.** Each body runs in an environment holding only its
  declared capabilities plus `std`, ambient `godot`, and a few safe primitives.
- **Custom `require`** that narrows a module to its `returns` contract.
- **Godot binding (ambient).** `godot.<Type>` resolves any Godot type — instances,
  properties, signals, enums/constants, statics — with exhaustive two-way Variant
  marshalling (primitives, every struct, packed arrays, collections).
- **Pre-bake source generator** (`[BindGodot]`) for zero-reflection static
  bindings.
- **`std.*` standard library** — C#-backed `vec3`, `vec2`, `color`, `vector`,
  `linked_list`.
- **Lifecycle hooks** (`on_start`, `on_update`, `on_physics_update`, `on_input`)
  wired to Godot's Node lifecycle, with one registration per hook project-wide.
- **Persistence (`save`)** — SQLite-backed runtime/player data under
  `~/.local/share/evaluate/`.
- **Hot reload (default)** — a `FileSystemWatcher` reloads scripts, configs, and
  declared assets on the main thread.
- Packaged for NuGet as `RadicalBeard.Evaluate`.

[0.3.0]: https://github.com/radical-beard/evaluate/releases/tag/v0.3.0
[0.2.0]: https://github.com/radical-beard/evaluate/releases/tag/v0.2.0
[0.1.0]: https://github.com/radical-beard/evaluate/releases/tag/v0.1.0
