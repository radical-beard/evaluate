# Changelog

## 0.10.0

Tracks the Evaluate 0.10.0 scripting contract (BREAKING in the runtime; the extension follows).

- **apis-as-modules.** The ambient `godot.*` table is gone: every capability is declared in
  frontmatter `apis:` and injected as its own bare global — framework services (`input`,
  `world`, `scene`, `save`, `sql`), host-registered C# extension apis, and Godot
  classes/enums (`apis: [Input, Node3D, Key]` → `Input.GetJoyAxis(...)`, `Node3D.new()`,
  `Key.Space`). `apis:` validation now accepts any PascalCase (Godot class/enum) entry, flags
  the removed `godot:`-prefixed form as an error, and flags the four blocked names
  (`DirAccess`, `FileAccess`, `ResourceLoader`, `ResourceSaver`) as errors — assets load via
  frontmatter `assets:`. With the generated spec present, `apis:` completion and hover also
  offer/describe every Godot class and global enum.
- **`assets:` is a map.** `<name>: "res-relative/path.ext"` (or a list of single-key maps),
  eagerly loaded and injected as the ambient `assets` table; filename `*` globs bind a
  stem-keyed table; `.gdshader` hot-reloads in place. Hover documents the new grammar, the
  key completes as a map, and bare-path list entries are flagged as errors.
- **New frontmatter keys** in completion, validation and hover: `properties:` (native Godot
  props applied to `self` at attach + reload), `behaviors:` (compose `*.behavior.evt` onto one
  node), `machines:` (`*.statemachine.evt`), `attributes:` (`{ base, min, max, regen,
  regen_delay, recover }`), `abilities:` (`*.ability` files granted at attach), and the state
  machine signature keys `name:` / `states:` / `initial:`.
- **New script types recognized.** `*.behavior.evt` (what `.node.evt` was — `.node.evt` stays
  as a deprecated alias) and `*.statemachine.evt` both use the node hook set and the `evt`
  language.
- **`params:` `dna` type** documented in hover (`"0x"` + exactly 16 hex digits; read via
  `params.<name>:trait(1..16)` / `:hex()`).
- Lua-body IntelliSense treats `params`/`assets` as known ambient globals alongside
  `self`/`config`.

## 0.9.0

- **Versioning realigned to the library (lockstep).** The extension now shares the library's
  version — the single source of truth is `<Version>` in `Directory.Build.props`, stamped into
  `package.json` at publish time — so a single `v<version>` tag releases both. No functional
  change from 0.1.2 (the jump to 0.9.0 is just the alignment).

## 0.1.2

- Frontmatter IntelliSense now knows the `require:` key: completion offers it, hover documents
  the `name: "path.evt"` binding grammar (list or map form), and the bundled key fallback
  includes it — matching the runtime's frontmatter `require:` feature (bind modules to sandbox
  locals up front, cycle-guarded, with two-way hot reload).

## 0.1.1

- Frontmatter IntelliSense now knows the node-script `params:` key: completion offers it
  (with a map snippet, not a list), hover documents the `<type> [= default]` grammar, and the
  bundled key fallback includes it — matching the runtime's per-node `params` feature.

## 0.1.0

Initial release.

- `.evt` language: YAML-frontmatter + Lua-body syntax highlighting.
- Frontmatter IntelliSense: completion, validation and hover for `apis:`/`register:`/keys,
  sourced from the generated `evaluate-api.json` (with a built-in fallback).
- Lua body IntelliSense via lua-language-server: the extension runs its own server and blanks
  the frontmatter from the synced text (document-sync middleware, identity position mapping),
  loading the generated LuaCATS type library and treating `*.evt` as Lua via configuration
  middleware (no changes to user settings).
- Sandbox-aware: body completion only offers the capability apis declared in `apis:`, and using
  an undeclared api in the body is flagged — mirroring the EvaLuate runtime sandbox.
- Marketplace-ready: the project logo as the 128×128 icon + gallery banner; `publish:ovsx`
  script and a `publish-extension` CI workflow that releases to Open VSX on a
  `vscode-v<version>` tag.
