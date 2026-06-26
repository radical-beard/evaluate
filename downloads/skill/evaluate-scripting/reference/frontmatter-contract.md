# Frontmatter contract

Every `.evt` / `.node.evt` script may begin with a `---`-delimited YAML block. It is the
**signature**: it declares what the script needs and exposes. Everything below the closing
`---` is the Lua body (its line numbers are preserved for error messages).

A script with no leading `---` is all body — valid, but it gets no capabilities and
registers no hooks.

```
---
config:
 - game.toml
apis:
 - save
 - scene
register:
 - on_start
 - on_update
scenes:
 - level1
returns:
 - spawn
 - hp: get double
assets:
 - res://art/hero.png
---
-- lua body here
```

## The keys

| Key | Value | Meaning |
|-----|-------|---------|
| `config` | list of TOML files | Each file becomes `config.<section>.<key>`. `player.toml` with `[player]` → `config.player.*`. Read live (hot-reloads). |
| `apis` | list of capability names | Adds each to the sandbox. Valid: `input`, `world`, `scene`, `save`, `sql`. Anything not listed here is **absent**. (`godot`/`std` are ambient — no declaration needed.) |
| `register` | list of hook names | Lifecycle hooks this script handles. Each name must have a matching global `function <name>(...)` in the body. See [lifecycle-hooks.md](lifecycle-hooks.md). |
| `returns` | list of member specs | For a script loaded via `require` — the contract of the returned module. See below. |
| `scenes` | list of scene names | **System scripts only.** Restricts the script's hooks to those scenes. Omit ⇒ **global** (runs in every scene). |
| `assets` | list of resource paths | Files to watch for hot-reload alongside the script. |

`config` and `apis` accept either YAML list form (`- game.toml`) or omitted/empty.

## The `returns` grammar

`returns:` declares the public shape of a module that other scripts pull in with
`require("module")`. The runtime **narrows** the returned table to exactly these members —
anything undeclared is unreachable, and a declared member the module forgot to expose is an
error at `require` time.

Two forms per entry:

- **Plain member** — `- spawn` — the module must return a table with a `spawn` field
  (usually a function). Exposed as-is.
- **Property** — `- hp: get set double` — a get/set accessor pair. The module must expose
  `get_hp` (for `get`) and/or `set_hp` (for `set`). Read-only is just `get` (omit `set`),
  and then no setter is exposed.

```
---
returns:
 - hp: get double      -- read-only: module exposes get_hp(), callers cannot set
---
local M, hp = {}, 100
function M.get_hp() return hp end
return M
```

The access spec is `get`/`set` tokens plus a trailing type name (the type is documentation —
there is no Lua type system; the contract is structural).
