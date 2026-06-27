# EvaLuate (`.evt`) — VS Code extension

Syntax highlighting, IntelliSense and autocomplete for **EvaLuate `.evt` scripts** — the
YAML-frontmatter-plus-sandboxed-Lua files you write to script a Godot game with
[EvaLuate](https://github.com/radical-beard/evaluate).

## What you get

- **Highlighting** — the leading `---` … `---` signature is highlighted as YAML; the body as
  Lua.
- **Frontmatter IntelliSense** — completion, validation and hover for the signature block:
  - `apis:` → the real capability list (`input`, `world`, `scene`, `save`, `sql`), with a
    warning on anything undeclared/unknown;
  - `register:` → the actual lifecycle hooks (system vs node hooks, picked by filename);
  - top-level keys (`config`/`apis`/`register`/`returns`/`assets`/`scenes`).
  These are read from your project's generated `evaluate-api.json` when present, so they track
  your exact EvaLuate version (a small built-in fallback is used otherwise).
- **Lua IntelliSense in the body** — completion, hover, signature help, go-to-definition and
  diagnostics over the **Lua part only** (the frontmatter is hidden from the analyzer), backed
  by [lua-language-server](https://luals.github.io/) and your generated LuaCATS types — so
  `godot.*`, `std.*`, `save`, `scene`, etc. autocomplete.
- **Sandbox-aware completion** — the body only autocompletes the capability apis this file
  actually declared in `apis:`. If you didn't declare `save`, `save` won't be suggested, and
  using it anyway gets a warning (`'save' is used but not declared in 'apis:'`) — mirroring the
  EvaLuate runtime, where an undeclared api is genuinely absent. `godot`/`std`/`self` are
  ambient and always available.

## Requirements

The Lua body features need a `lua-language-server` binary. The easiest way is to install the
**[Lua](https://marketplace.visualstudio.com/items?itemName=sumneko.lua) (sumneko)** extension
— this extension reuses its bundled server. Alternatively set `evt.lua.serverPath` to a
`lua-language-server` executable. (Highlighting and frontmatter IntelliSense work with no
server.)

## Get the most out of it — generate the type library

The whole point is autocomplete over the *actual* `godot.*`/`std.*` API. Generate it from your
project (EvaLuate ≥ the `--emit-api` release):

```
godot --headless --path . -- --emit-api downloads/spec
```

That writes `downloads/spec/evaluate-api.json` (drives frontmatter completion) and
`downloads/spec/luacats/*.lua` (drives Lua-body completion). The default settings already point
at `downloads/spec/…`; adjust `evt.lua.libraryPaths` / `evt.spec.path` if you put them
elsewhere. Re-run after upgrading Godot or EvaLuate.

## Settings

| Setting | Default | Purpose |
|---|---|---|
| `evt.lua.enable` | `true` | Lua IntelliSense in the body. |
| `evt.lua.serverPath` | `""` | Path to a `lua-language-server` (else auto-detect sumneko's). |
| `evt.lua.libraryPaths` | `["downloads/spec/luacats"]` | LuaCATS folders to load as the type library. |
| `evt.spec.path` | `downloads/spec/evaluate-api.json` | The generated spec that drives frontmatter completion. |

## How it works

`.evt` is a hybrid file, so the extension runs its own `lua-language-server` and, via the
language-client's document-sync middleware, blanks the frontmatter out of the text it syncs to
the server — line-for-line, exactly what the EvaLuate runtime does. Because blanking preserves
line and column numbers, every position maps 1:1, so completion/hover/diagnostics come back
keyed to the real `.evt` file with no remapping. The server is told (via configuration
middleware, without touching your settings) to treat `*.evt` as Lua and to load the generated
LuaCATS library. The frontmatter gets its own EvaLuate-aware language features (it isn't Lua).

## Install

- **From a `.vsix`:** `code --install-extension evaluate-evt.vsix` (or *Extensions ▸ … ▸ Install
  from VSIX*).
- **Build it yourself:** `npm install && npm run package` produces `evaluate-evt.vsix`.

## Develop

```
npm install
npm run watch        # rebuild on change
# press F5 in VS Code to launch the Extension Development Host, open an .evt file
```

## License

MIT OR Apache-2.0, matching EvaLuate.
