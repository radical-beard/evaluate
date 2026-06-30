<p align="center"><img src="logo.png" alt="Evaluate" width="180"></p>

# Evaluate

A data-driven, moddable game framework built as an extension that runs **inside
Godot** (Godot is the host; Evaluate has no `main`). Engine-side code is .NET/C#
(`net10.0`, Godot 4.6-mono). Gameplay is authored in "evt" scripts whose YAML
frontmatter declares a capability-scoped *signature*; a custom loader sandboxes
each script to exactly what it declares. Scripts are auto-discovered and
hot-reloaded by default.

## Architecture

- **Frontmatter → signature.** The leading `---` block is split off and parsed
  as real YAML (YamlDotNet) into `config:`, `apis:`, `register:`, `returns:`,
  `params:`, `assets:`, `scenes:`; the Lua body is handed to the VM verbatim (line numbers
  preserved).
  The signature *is* the C#↔Lua boundary contract — `returns` declares typed,
  access-scoped members (no Lua type system). (`src/Evaluate/Frontmatter.cs`)
- **Per-script sandbox.** Each body runs via `load(body, name, "t", env)` on a
  shared `LuaState`, where `env` holds *only* the declared `config.*`, declared
  `apis`, the always-available `std` and ambient `godot`, plus a few safe
  primitives — including the metatable builtins (`setmetatable`/`getmetatable`/
  `rawget`/`rawset`/`rawequal`/`rawlen`) so scripts can define classes the
  idiomatic Lua way. Undeclared globals (`os`, `io`, …) are absent, and `pcall`
  is intentionally withheld (errors surface rather than being swallowed).
  (`src/Evaluate/Loader.cs`)
- **Custom `require`** narrows the returned module to its `returns` contract
  (get/set property → `get_`/`set_` accessors; plain method; read-only hides the
  setter; missing accessor errors).
- **Data lives in the engine.** Game objects *are* Godot nodes — declared in scene
  files or created via `godot.<Type>.new()` / `world` — so there is no separate
  entity system. The Lua handle is a thin proxy whose metatable routes reads/writes
  into the engine. (`src/Evaluate/GodotBinder.cs`)
- **Auto-discovery.** The runtime recursively scans `res://scripts`; any system
  `.evt` with a `register:` block is wired to the Godot lifecycle. A hook may be
  registered **once per scene** (see *Scenes & layers*) — so the same `on_update`
  can be registered in `menu`, `level1`, and globally, each by a different script.
- **Hot reload (default).** A `FileSystemWatcher` watches scripts, scene files,
  configs, and frontmatter-declared `assets:`; changes reload on the main thread.
  A changed system or node script re-runs its body and refreshes its hook
  closures while live nodes persist; a changed `*.scene` rebuilds the active
  scene. (`src/Evaluate/EvaluateRuntime.cs`)
- **Scenes & layers.** Gameplay is split into a persistent **global layer** and a
  swappable **scene layer**, so one program holds many scenes, each with its own
  registered functions:
  - **`global.scene`** (reserved manifest) declares nodes that *never* clear
    (e.g. the player) plus `start_scene`. Loaded once into a persistent
    **Global root**.
  - **`*.scene`** files (TOML content) declare a node tree as keyed, nested tables —
    `[nodes.Player]` then `[nodes.Player.Camera]` makes Camera a child of Player.
    A node's name is the table key; reserved keys `type`/`script`; a **sub-table is
    a child node** and a **scalar/array is a property** (`position = [x, y, z]`). A
    sub-table tagged with **`_type`** is a *property*, not a child — it names an inline
    resource (`mesh = { _type = "BoxMesh", size = [1,1,1] }`) **or a builtin struct**
    (`custom_aabb = { _type = "Aabb", position = [1,2,3], size = [4,5,6] }`), which is how
    composite structs like `Transform3D`/`Rect2`/`Basis`/`Projection` are written. A
    property key may be quoted to carry a `/` (Control theme overrides:
    `"theme_override_colors/font_color" = [..]`). An optional top-level
    **`description = "…"`** holds scene docs as data. Parsed with Tomlyn; instantiated
    under a per-scene container that is **freed wholesale** on switch. The editor addon
    can round-trip all of this back to the `.scene` (see below).
  - **`*.node.evt`** is one node's behavior, attached via the scene file; its
    hooks run with **`self`** bound to that node (no spawning in the script). It may
    declare a **`params:`** block — typed, per-instance values the scene supplies via
    `params = { … }` on the node and the body reads through the ambient **`params`**
    table. The per-node analogue of `config` (which is shared): `params:` entries are
    `name: <default>` (type inferred), `name: <type>` (required), or
    `name: "<type> = <default>"`; the loader fills defaults, type-checks each supplied
    value, and rejects an undeclared or missing-required param — the same
    signature-is-the-contract stance as the sandbox. Stashed as `__evt_params` so the
    editor round-trips it.
  - **`*.evt`** systems are conductors: no `scenes:` ⇒ **global** (always run);
    `scenes: [a, b]` ⇒ active only while `a`/`b` is current. The `scene` API does
    routing — `scene.change(name)` (applied at the next frame boundary, never
    mid-hook), `scene.current()`, `scene.find(path)` (unique by node path, e.g.
    `"Level/Enemy"`), `scene.add(node)`.
- **Editor preview + write-back (optional addon).** A `.scene` file is TOML, which the
  Godot editor doesn't render natively. The `dev/addons/evaluate_scene` addon registers an
  `EditorImportPlugin` that converts `.scene` → a `PackedScene` (via the *same*
  `SceneFile`/`SceneBuilder` the runtime uses), so the editor shows and renders the node
  tree. It also adds an **"Evaluate" dock** (and Tools-menu entries) to open a `.scene` as
  an *editable* native scene, edit it with the normal editor — move nodes, create and place
  new nodes, set properties, attach a node script via a `__evt_script` metadata entry — and
  **Save to .scene**, which serializes the edited tree back to the TOML file via
  `SceneWriter` (the inverse of `SceneFile`/`SceneBuilder`). Editor-only (`#if TOOLS`) and
  needs no running game (a running game's hot-reload picks the saved file up for free). The
  addon is **not** part of the NuGet package (the runtime library carries no editor types);
  instead it ships as a drop-in zip on each [GitHub release](https://github.com/radical-beard/evaluate/releases)
  (`evaluate_scene-<version>.zip`): extract it into your project to get
  `res://addons/evaluate_scene/`, then enable the plugin in Project Settings (it needs the
  `RadicalBeard.Evaluate` package for the build/serialize logic). A C# editor addon can't be a
  bare DLL — its `.cs` must live under `res://addons/` and compile with the game — so the zip
  carries the source; the stable `Evaluate.Editor` namespace means it drops in verbatim.
- **Lifecycle hooks.** `register:` wires Godot's Node lifecycle. **System hooks:**
  `on_start` (global, once), `on_enter`/`on_exit` (scene-scoped, per activation),
  `on_update(dt)`, `on_physics_update(dt)`, `on_input(event)`. **Node hooks**
  (`*.node.evt`, with `self`): `on_ready`, `on_update`, `on_physics_update`,
  `on_input`, `on_exit`.
- **`std.*` standard library.** Real C#-backed types via the `[LuaObject]` source
  generator — `std.vec3`, `std.vec2`, `std.color`, `std.vector`,
  `std.linked_list` (`src/Evaluate/Std.cs`).
- **Persistence (`save`).** SQLite-backed runtime/player data in Godot's
  per-project `user://` directory (the platform-native, per-game path Steam Cloud
  syncs from) — `save.set/get/delete` (`src/Evaluate/Persistence.cs`).
- **Godot binding (ambient, default).** `godot.<Type>` resolves any Godot type
  on first use (`src/Evaluate/GodotBinder.cs`):
  - **instances** — `godot.Node3D.new()` (Activator); member access routes
    through the engine ClassDB (`GodotObject.Call/Get/Set`), not reflection.
    Instances are `ILuaUserData`, so they pass back into the engine
    (`world:add_child(node)`);
  - **properties** — `node.position = std.vec3(…)` / `node.position`;
  - **signals** — `node:connect("timeout", fn)` (0–6 args, dispatched on the
    main thread) and `node:emit_signal(...)`;
  - **enums/constants** — `godot.Key.Space`, `godot.Timer.TimerProcessCallback.Idle`;
  - **marshalling** — exhaustive: primitives, strings, `NodePath`, objects,
    `Array`/`Dictionary`; rich C#-backed `Vector2/3` & `Color` (↔ `std.*`); and
    every other struct (`Vector4`, `Vector2I/3I`, `Quaternion`, `Rect2`, `Plane`,
    `Aabb`, `Basis`, `Transform2D/3D`) + packed arrays as named-field/list tables.
    Reads are tables; writes are **target-type-aware** — assigning a table to a
    struct property builds the exact struct (so `node.transform = t` round-trips);
  - **statics** — reflection fallback, or a **pre-baked** zero-reflection binding.
- **Pre-bake (source generator).** `generator/` is a Roslyn
  `IIncrementalGenerator`: `[BindGodot(typeof(Godot.OS))]` emits `OsBinding.Create()`
  with direct, reflection-free calls (53 bound `OS` methods), filtered to
  Lua-convertible signatures. The binder prefers pre-baked bindings.
- **API spec & agent skill (consumer onboarding).** `godot --headless --path . --
  --emit-api <dir>` dumps the *entire* Lua surface a script sees — read live, so nothing is
  hand-written or hard-coded: `godot.*` classes/methods/properties/signals come from engine
  `ClassDB` introspection, enums/constants/statics + the class set from GodotSharp reflection,
  `std.*` from the `[LuaObject]` types, the capability apis by walking the tables `Loader`
  builds, and hooks/frontmatter from the runtime's arrays (`src/Evaluate/EvaluateDocs.cs` —
  the runtime analogue of the build-time generator). It emits `evaluate-api.json`, LuaCATS
  `---@meta` files for IDE autocomplete (the whole `godot.*`/`std.*` surface), and a Markdown
  reference. A pre-generated copy plus a drop-in **agent skill** (teaching an LLM the script
  contract) live under `downloads/`; each release also ships `evaluate-downloads-<version>.zip`.

## Base VM

[Lua-CSharp](https://github.com/nuskey8/Lua-CSharp) (`LuaCSharp` 0.5.5, Lua 5.2).
Source-generator interop (`[LuaObject]`) → AOT-clean, low-allocation. Its API is
async-only; we bridge to sync for Godot's `_Process` (safe because script calls
don't actually await). Dialect note: scripts use `x = x + …`, not the planned
custom dialect's `+=`.

## Run

    godot --headless --path dev                       # demo: global layer, scene switch (menu -> level1), node script
    godot --headless --path dev -- --test             # enforcement suite (18 tests)
    godot --headless --path dev -- --quit-after 8     # demo, then quit after 8 frames
    godot --headless --path dev -- --emit-api out/    # dump the full Lua API spec (json + LuaCATS + markdown)

## Layout

    README.md  CHANGELOG.md  Directory.Build.props  Evaluate.slnx  global.json  LICENSE.md
    src/Evaluate/            Evaluate.csproj  EvaluateRuntime.cs  Loader.cs  Std.cs
                             Frontmatter.cs  Toml.cs  SceneFile.cs  SceneBuilder.cs
                             GodotBinder.cs  Persistence.cs  BindGodotAttribute.cs  Prebaked.cs  EvaluateTests.cs
                             EvaluateDocs.cs  EvaluateDocsModel.cs  EvaluateDocsWriters.cs   (--emit-api spec generator)
    src/Evaluate.Generator/  Evaluate.Generator.csproj  BindGodotGenerator.cs   (Roslyn source generator)
    downloads/  drop-in resources for consumers: spec/ (generated Lua API spec — json + LuaCATS
                + markdown) and skill/evaluate-scripting/ (a downloadable agent skill)
    editors/vscode/  VS Code extension for .evt: YAML+Lua highlighting, frontmatter IntelliSense,
                and sandbox-aware Lua-body autocomplete via lua-language-server + the LuaCATS spec
    dev/        the demo / enforcement-test harness game (consumes the lib by project reference):
      project.godot  main.tscn  EvaluateHost.cs  Dev.csproj
      scripts/  global.scene  menu.scene  level1.scene
                player.node.evt  enemy.node.evt (per-node params)  showcase.evt  menu.evt  level1.evt  game.toml  player.toml
      addons/evaluate_scene/  editor import plugin (.scene -> PackedScene preview)
      tests/    enforcement suite (.evt): forbidden_global / undeclared_api / missing_accessor /
                system_a / system_b / readonly_module / metatable_oop / scene_a / scene_b /
                scene_a_dup / global_update / self_node, + global.scene
    artifacts/  packed .nupkgs (gitignored)

## Remaining work toward "full featured"

- **Godot coverage is complete**: instance + static members (any type), all
  Variant structs + packed arrays, enums/constants, and signals — all two-way.
  Static methods with struct/packed signatures bind via the reflection fallback
  (layered over the pre-baked primitives). Instance access is reflection-free
  (engine ClassDB `Call/Get/Set`). Remaining (perf only): extend the source-gen
  pre-bake to struct/packed static signatures so they too avoid reflection.
- **Frontmatter LSP** (deferred): editor diagnostics for the signature block —
  e.g. red-underline a non-existent `godot.*` API or missing config file. The
  Lua body itself rides the standard Lua LSP.
- **AOT export & mod packaging/distribution**: not yet built.
- Out of scope by decision: a Lua type system (signature contract is enough) and
  sandbox resource limits / DoS protection (modding is free).
