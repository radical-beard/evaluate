# Changelog

All notable changes to Evaluate are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While
the version is `0.x`, minor bumps may include breaking changes.

## [0.7.2] — 2026-06-24

### Fixed
- **Runtime struct reads are named-field again** (regression from 0.7.0). The source-gen
  refactor routed the runtime/script struct-read path (`ToLua`) through the serialization
  codec, so long-tail structs (`Rect2`, `Aabb`, `Plane`, `Quaternion`, `Vector4`, `Basis`,
  `Transform2D/3D`, `Projection`) came back as the codec's positional/`_type` repr — making
  `self.region_rect.size.x`, `self.transform.basis.z`, etc. read `nil`. The runtime API is
  now decoupled from serialization: scripts get **named-field** tables (`{x=…}`,
  `{position={…}, size={…}}`, basis as `{x,y,z}` columns), while `.scene` files keep the
  positional / `_type` form (a bare `{x,y,z}` table in a file would parse as a child node).
  `Vector2`/`Vector3`/`Color` were unaffected (rich `std`-backed types).

## [0.7.1] — 2026-06-24

Editor-addon distribution + a follow-up fix.

### Added
- **The `evaluate_scene` editor addon is now distributed as a drop-in zip** attached to each
  GitHub release (`evaluate_scene-<version>.zip`, built by the `release-addon` workflow). A
  game installs it without building from source: download, extract into the project (gives
  `res://addons/evaluate_scene/`), and enable the plugin. Requires the `RadicalBeard.Evaluate`
  package (≥ this version). Also suitable for a Godot AssetLib entry.

### Changed
- **Addon namespace is now the stable `Evaluate.Editor`** (was the per-game `EvaluateDev`),
  so the folder drops into any project verbatim — no more per-game namespace edits.

### Fixed
- Nullable warnings in the struct-codec recompose call sites (pass the non-null
  `TableToObjModel(t)`); no behavior change.

## [0.7.0] — 2026-06-24

Godot editor ↔ `.scene` write-back. Edit Evaluate scenes in the Godot editor — move
nodes, create and place new nodes, set property values, attach node scripts — and save
the result back to the custom `.scene` (TOML) file. Editor-only: a running game is never
required (if one is running, its hot-reload picks up the saved file for free).

### Added
- **`SceneWriter`** — serializes a live Godot node tree back to a `.scene` file (TOML),
  the inverse of `SceneFile` + `SceneBuilder`. Emits clean, minimal files: only properties
  that differ from the type's defaults (or that the author originally wrote) are written,
  spatial transforms emit as the `position`/`rotation`/`scale` triple, floats format
  shortest (`2.2`, not `2.2000000476837`). Full fidelity for the `.scene` feature set —
  `script`, `groups`, `meta`, `unique`, `connections`, `instance=` (not re-inlined), and
  inline `_type` / `res://` resources all round-trip. Public API: `WriteContainer`,
  `Write`, `WriteNode`.
- **Editor write-back workflow** (`evaluate_scene` addon) — an "Evaluate" dock (and Tools
  menu items) to open a `.scene` as an editable native scene (built with the same
  `SceneBuilder` the runtime uses), edit it with the normal Godot tools, and **Save to
  .scene**. The read-only import preview is unchanged and coexists.
- **Full builtin-type fidelity, source-generated** — `_type` now names a **struct** as well
  as a resource, so composite structs that have no plain-table form (a bare `{…}` parses as a
  child node) can live in a `.scene`:
  `custom_aabb = { _type = "Aabb", position = [1,2,3], size = [4,5,6] }`,
  `transform = { _type = "Transform3D", basis = { _type = "Basis", row0 = [1,0,0], row1 = [0,1,0], row2 = [0,0,1] }, origin = [5,2,3] }`.
  An all-scalar struct (a vector/color) is a positional array; a composite struct is a nested
  `_type` table. The conversions are **emitted at build time by a source generator** that
  enumerates every Godot builtin struct from the GodotSharp reference — so all 16
  (`Vector2/2i/3/3i/4/4i`, `Color`, `Quaternion`, `Rect2/2i`, `Plane`, `Aabb`, `Basis`,
  `Transform2D/3D`, `Projection`) are covered with **no hand-written per-type code and no
  runtime reflection** (AOT/trim safe). A new Godot builtin is picked up automatically.
- **`/`-keyed properties** (e.g. Control theme overrides like
  `theme_override_colors/font_color`) round-trip — written as quoted TOML keys and applied
  via `obj.Set("a/b", v)`.
- **Scene `description`** — an optional top-level `description = "…"` (sibling of
  `start_scene`), parsed into `SceneSpec.Description` and round-tripped as data. Scene docs
  live here instead of in comments, so they survive an editor save; the editor carries it on
  the edited root's metadata.

### Changed
- **Scene `groups` are now persistent** (`AddToGroup(persistent: true)`), so a group
  survives being packed and shows in the editor's Groups tab. Runtime behavior
  (`is_in_group`, group lookup) is unchanged.

### Internal
- `SceneBuilder.BuildNode` stashes the facts a bare node can't carry (`script`,
  `instance`, the author's original property keys, declarative `connections`) as reserved
  `__evt_*` node meta, which `SceneWriter` reads back and never emits as user `meta`.
- New `StructCodecGenerator` (in `Evaluate.Generator`) emits `GodotStructCodec` —
  reflection-free `Decompose`/`Recompose` for every builtin struct, discovered from the
  Variant type enum. Field-backed structs round-trip via their settable fields; the few
  property-backed ones (`Rect2`/`Aabb`/`Plane`/`Rect2i`) via their name-matching constructor.
  `GodotBinder` and `SceneWriter` consume it instead of hand-written per-struct switches.

## [0.6.0] — 2026-06-22

### Changed
- **Renamed the once-only node hook `on_ready` → `on_attach`** (breaking). Now that `on_load`
  runs on *every* (re)load, `on_ready` was ambiguous; `on_attach` makes the lifecycle clear:
  it fires ONCE, when the node + its script first attach to the tree (never on reload), while
  `on_load` fires right after it AND on every hot reload. Migration: change `on_ready` to
  `on_attach` in `register:` blocks and the function name. (Things that should happen once go
  in `on_attach`; rebuildable/imperative setup you want to hot-reload goes in `on_load`.)

## [0.5.2] — 2026-06-22

Hot-reload + app-lifecycle hooks.

### Added
- **`on_load` (node + system scripts)** — runs after `on_ready`/`on_start` on first load
  AND on every hot reload (of the script OR a declared config `.toml`). The place for
  idempotent setup you want to hot-reload — imperative builders (UI, environments) now
  rebuild live instead of needing a restart. `on_ready`/`on_start` stay once-only.
- **`on_unload` (node + system scripts)** — fires right before a reload tears the old setup
  down (its closures are still valid), to release what `on_load` built (free spawned nodes,
  disconnect signals, stop timers). Reload sequence: `on_unload` → body re-runs → `on_load`.
- **`on_focus_in` / `on_focus_out`** — application focus changes (e.g. flush/autosave on
  alt-tab, pause-on-focus-loss).
- **`on_pause` / `on_resume`** — SceneTree pause / unpause.

## [0.5.1] — 2026-06-22

### Fixed
- **`instance =` cycles are rejected instead of crashing.** A scene that instances itself
  (directly or via a mutual A→B→A chain) recursed forever into an uncatchable
  StackOverflow at scene build. `SceneBuilder.BuildNode` now threads a visited-set across
  the instance chain and throws a clean `EvaluateException` (caught + logged by the runtime)
  on a revisit. Child nesting and reusing the same instanced scene in sibling branches are
  unaffected.

## [0.5.0] — 2026-06-22

Full Godot-scene parity for `.scene` files, an app-quit hook, and raw SQL for scripts.

### Added
- **Scene-file node `meta` and `groups`.** A `[nodes.X]` may declare
  `meta = { key = value, ... }` (→ `set_meta`) and `groups = ["a", "b"]`
  (→ `add_to_group`) — reserved keys, so a node can't be named `meta`/`groups`.
- **Scene-file resources.** A string property holding a `res://…` path that targets a
  `Resource`-typed property is loaded (e.g. `texture = "res://art/x.png"`); an inline
  sub-resource is built from `{ _type = "BoxMesh", size = [1, 1, 1] }` (the `_type`
  marker distinguishes it from a child node).
- **Scene instancing, unique names, connections.** `instance = "other_scene"` builds that
  scene's root nodes as children; `unique = true` sets `unique_name_in_owner` (the loader
  now sets node owners, so `%Name` resolves at runtime); `connections = [{ signal, to,
  method }]` wires a node's signal to a built-in method on the node at `to`.
- **`on_quit` global hook.** Fires once on any teardown (window-close, a script's
  `get_tree():quit()`, `--quit-after`) so global systems/nodes can flush persistence.
  Window-close is no longer auto-accepted; the runtime quits explicitly.
- **`sql` capability — full SQL for scripts.** `apis: - sql` exposes
  `sql.exec` / `sql.query` / `sql.query_row` / `sql.transaction` (synchronous,
  parameterized via `@p1, @p2, …`), `sql.exec_async` + `sql.flush` (a background writer
  thread that never touches the Lua state), and `sql.snapshot(path)`. WAL mode makes
  commits atomic and durable; the game owns its schema. The flat `save` kv API is unchanged.

## [0.4.2] — 2026-06-19

Config and scene hot-reload now actually apply live.

### Changed
- **`config` is a live view, not a frozen snapshot.** Each `config.<section>.<key>`
  access reads the current TOML cache, so saving a `.toml` updates running scripts
  immediately — even values captured once into an object (`self.cfg = config.x`;
  then `cfg.field` each frame). Previously a script's `config` was copied at load
  time, so a bare `.toml` edit never took effect without re-running the script.
  `ReloadOnChange` now re-reads a changed `.toml` into the cache (keeping the
  last-good values if the file is mid-save / malformed) rather than just
  invalidating it.
  - Minor: `config.<section>` is now backed by a metatable, so `pairs()`/`ipairs()`
    over a config section no longer enumerates its keys (named-key access is
    unchanged). Read declared keys by name.
- **`global.scene` hot-reloads the persistent layer in place.** Editing the global
  manifest now fires `on_exit`, frees the manifest's nodes, and re-instantiates +
  fires `on_ready` — instead of logging "restart to apply". Note this *recreates*
  the persistent nodes, so their runtime state resets (it applies a structural
  manifest change live; it is not state-preserving like a node-script reload).

### Fixed
- **Scene rebuilds keep their node name.** A reloaded active scene (and the
  rebuilt global layer) now detaches the old container/nodes before re-adding, so
  the rebuilt nodes keep their declared names instead of colliding into
  `Name2`-style suffixes while the old subtree waits to free.

## [0.4.1] — 2026-06-18

Per-game save location.

### Changed
- **Breaking — saves live in Godot's `user://` directory**, not a hardcoded
  `~/.local/share/evaluate/save.db`. `Persistence` now resolves its SQLite store
  via `ProjectSettings.GlobalizePath("user://")`, so each game gets its **own**
  store at the **platform-native** per-app path (Windows `%APPDATA%`, macOS
  `~/Library/Application Support`, Linux `~/.local/share`) — which is also what
  Steam Cloud syncs from. The old path was Linux-only *and* shared the same
  `save.db` across every Evaluate game. A game picks a clean folder name with
  `use_custom_user_dir` / `custom_user_dir_name` in `project.godot`. (Existing
  data under the old path is not migrated; nothing reads it anymore.)

## [0.4.0] — 2026-06-18

Nested node trees, a real TOML parser, and editor preview for scene files.

### Added
- **Nested node trees.** Scene files use a keyed/nested schema — a node's name is
  the table key and nesting is the tree: `[nodes.Player]` then
  `[nodes.Player.Camera]` makes `Camera` a child of `Player`, to any depth. A
  sub-table is a child node; a scalar/array is an engine property; `type`/`script`
  are reserved keys.
- **Editor preview addon** (`dev/addons/evaluate_scene`). An `EditorImportPlugin`
  converts `.scene` → a `PackedScene` so the Godot editor recognizes and renders
  the node tree. It reuses the same `SceneFile`/`SceneBuilder` as the runtime, is
  editor-only (`#if TOOLS`), and never touches the runtime path — so runtime
  hot-reload of `.scene` edits is unaffected.
- **`SceneBuilder`** — shared, runtime-only node-tree builder used by both the
  runtime (live instantiation) and the editor importer (`PackedScene` packing).

### Changed
- **Breaking — scene files use the `.scene` extension** (TOML content), not
  `.scene.toml`. Godot's importer keys on the last extension, so `.scene` lets the
  editor plugin hook scene files only and never touch config `.toml`. The manifest
  is now `global.scene`.
- **Breaking — keyed/nested scene schema** replaces the flat `[[node]]` list with
  `name`/`parent` fields. Hierarchy is expressed by table nesting, not `parent`
  references (which are gone).
- **Breaking — `scene.find(path)` is path-based**, resolved against the live node
  tree (`"Level/Enemy"`), so lookups are unique by construction and never stale.
  The bare-name registry is gone.
- **TOML is now parsed by [Tomlyn](https://github.com/xoofx/Tomlyn) 0.19.0**, both
  for config and scene files; the hand-rolled `Toml.cs` parser is gone. Used via
  Tomlyn's document model (no reflective deserialization), so the AOT-clean posture
  holds.
- Enforcement suite grew from 12 to 18 tests (nested parse, `PackedScene` build,
  unique path-based `find`, malformed-TOML handling, reserved-name rejection,
  sub-table-as-child, nested-table marshalling).

### Hardened
- **Malformed TOML degrades, it does not crash.** A bad config or `.scene` (e.g. a
  half-saved hot-edit, a typo, or a duplicate key) now surfaces as a clean
  `EvaluateException` and is logged + skipped — the running game keeps going, the
  startup continues, and one broken script no longer stops the others. (The old
  hand-rolled parser silently ignored bad lines; Tomlyn throws, so the throw is now
  caught at every parse boundary.)
- **Node names are validated** against Godot's reserved path characters
  (`. : @ / % "`) at parse time, so a name can't be silently sanitized and break
  path-based `scene.find`.
- **Nested/inline config tables marshal correctly** to Lua tables instead of being
  silently dropped to `nil`.
- `scene.change` to a missing/empty scene logs a warning instead of silently
  activating an empty scene.

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

[0.4.1]: https://github.com/radical-beard/evaluate/releases/tag/v0.4.1
[0.4.0]: https://github.com/radical-beard/evaluate/releases/tag/v0.4.0
[0.3.0]: https://github.com/radical-beard/evaluate/releases/tag/v0.3.0
[0.2.0]: https://github.com/radical-beard/evaluate/releases/tag/v0.2.0
[0.1.0]: https://github.com/radical-beard/evaluate/releases/tag/v0.1.0
