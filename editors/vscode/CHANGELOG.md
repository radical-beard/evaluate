# Changelog

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
