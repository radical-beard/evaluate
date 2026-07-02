# EvaLuate (`.evt`) — VS Code extension

Syntax highlighting, IntelliSense and autocomplete for **EvaLuate `.evt` scripts** — the
YAML-frontmatter-plus-sandboxed-Lua files you write to script a Godot game with
[EvaLuate](https://github.com/radical-beard/evaluate).

## What you get

- **Highlighting** — the leading `---` … `---` signature is highlighted as YAML; the body as
  Lua.
- **Frontmatter IntelliSense** — completion, validation and hover for the signature block:
  - `apis:` → the framework services (`world`, `scene`, `save`, `sql`, `actions`,
    `controller`, `store`) plus — since 0.10.0 — every Godot class/enum by PascalCase name
    (each injected as a bare global); unknown lowercase names warn, the removed `godot:`
    prefix is an error, and so are the blocked names — the file/resource-IO classes
    (`DirAccess`/`FileAccess`/`ResourceLoader`/`ResourceSaver`) and, since 0.11.0, the whole
    raw-input surface (`Input`, `Key`, `JoyButton`, `InputEvent*`, …: input is native — use
    the `actions` api);
  - `register:` → the actual lifecycle hooks (system vs node hooks, picked by filename —
    `*.behavior.evt`, `*.statemachine.evt` and the deprecated `*.node.evt` get node hooks);
  - all top-level keys (`config`/`apis`/`register`/`returns`/`params`/`require`/`assets`/
    `scenes`/`properties`/`behaviors`/`machines`/`attributes`/`abilities`/`name`/`states`/
    `initial`).
  These are read from your project's generated `evaluate-api.json` when present, so they track
  your exact EvaLuate version (a small built-in fallback is used otherwise).
- **Lua IntelliSense in the body** — completion, hover, signature help, go-to-definition and
  diagnostics over the **Lua part only** (the frontmatter is hidden from the analyzer), backed
  by [lua-language-server](https://luals.github.io/) and your generated LuaCATS types — so
  declared Godot classes, `std.*`, `save`, `scene`, etc. autocomplete.
- **Sandbox-aware completion** — the body only autocompletes the capability apis this file
  actually declared in `apis:`. If you didn't declare `save`, `save` won't be suggested, and
  using it anyway gets a warning (`'save' is used but not declared in 'apis:'`) — mirroring the
  EvaLuate runtime, where an undeclared api is genuinely absent. `std`/`self`/`config`/
  `params`/`assets` are ambient.

## Requirements

The Lua body features need a `lua-language-server` binary. The easiest way is to install the
**[Lua](https://marketplace.visualstudio.com/items?itemName=sumneko.lua) (sumneko)** extension
— this extension reuses its bundled server. Alternatively set `evt.lua.serverPath` to a
`lua-language-server` executable. (Highlighting and frontmatter IntelliSense work with no
server.)

## Get the most out of it — generate the type library

The whole point is autocomplete over the *actual* Godot-class/`std.*`/capability API. Generate
it from your project (EvaLuate ≥ the `--emit-api` release):

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

## Publishing

The package is marketplace-ready (icon, metadata, bundled build). We publish to
**[Open VSX](https://open-vsx.org)** — the registry used by VSCodium, Cursor, Windsurf,
Gitpod and Theia. (No credit card; you log in with GitHub.)

**One-time setup:**

1. Sign in at <https://open-vsx.org> with GitHub, then sign the Eclipse Foundation Open VSX
   Publisher Agreement (Settings ▸ Profile).
2. Create an access token at <https://open-vsx.org/user-settings/tokens>.
3. Create the publisher namespace (must match `publisher` in `package.json`, `radical-beard`):
   ```
   npx ovsx create-namespace radical-beard -p <token>
   ```
4. Store the token as a GitHub Actions secret so CI can publish:
   ```
   gh secret set OVSX_PAT --repo radical-beard/evaluate
   ```
   (Run it from anywhere; it prompts for the value so the token never lands in your shell
   history. Drop `--repo` if you run it inside a clone of this repo.)

**Release (CI):** bump `version` in `package.json`, then tag — the `publish-extension`
workflow builds and publishes to Open VSX:

```
git tag vscode-v0.1.0 && git push origin vscode-v0.1.0
```

**Or publish from your machine:**

```
npm run package
npx ovsx publish evaluate-evt.vsix -p <token>
```

**No account is needed just to use it** — share the `.vsix` and
`code --install-extension evaluate-evt.vsix`.

> Want the VS Code Marketplace too (reaches stock VS Code)? It needs an Azure DevOps PAT.
> Create a publisher at <https://marketplace.visualstudio.com/manage>, then
> `npx @vscode/vsce publish --no-dependencies --packagePath evaluate-evt.vsix -p <pat>`
> (and add the matching step back to the workflow).

## License

MIT OR Apache-2.0, matching EvaLuate.
