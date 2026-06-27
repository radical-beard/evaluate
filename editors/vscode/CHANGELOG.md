# Changelog

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
- Marketplace-ready: 128×128 icon + gallery banner; `publish` / `publish:ovsx` scripts and a
  `publish-extension` CI workflow that releases to the VS Code Marketplace and Open VSX on a
  `vscode-v<version>` tag.
