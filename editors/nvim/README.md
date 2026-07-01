# evaluate.nvim

Neovim support for **Evaluate** `.evt` scripts — YAML-frontmatter + sandboxed-Lua-body files
that run inside Godot. The Neovim counterpart of `editors/vscode`.

- **Filetype + embedded highlighting.** `*.evt` / `*.node.evt` → the `evt` filetype; the
  frontmatter highlights as YAML, the body as Lua.
- **Frontmatter IntelliSense** (no external server):
  - **Completion** (via `omnifunc`) — frontmatter keys at the top level, capability `apis:` and
    lifecycle `register:` hooks under their sections (already-listed values are filtered out).
  - **Diagnostics** — unknown key / api / hook, and a capability used in the body but not
    declared in `apis:` (nil at runtime, since the sandbox only exposes declared apis).
  - **Hover** — docs for a frontmatter key or api under the cursor.
  All of it reads the generated `downloads/spec/evaluate-api.json` (auto-discovered), so it
  tracks the exact Godot + Evaluate in your project; a bundled fallback covers the stable names.
- **Lua-body IntelliSense** (needs `lua-language-server`). Attaches lua_ls to the `.evt` buffer
  but **blanks the frontmatter** in the text it sends — line-for-line, exactly what the runtime
  loader does — so the Lua body keeps its real line/columns and completion/hover/diagnostics map
  1:1. The generated LuaCATS library (`downloads/spec/luacats/`) is loaded automatically, so
  `godot.*`, `std.*`, `save`, `scene`, … all resolve.

## Requirements

- Neovim ≥ 0.10 (uses `vim.fs.root`, `vim.diagnostic`, `vim.uv`).
- Optional: `lua-language-server` on `PATH` for Lua-body IntelliSense. Without it, everything
  except the body LSP still works.

## Install

With [lazy.nvim](https://github.com/folke/lazy.nvim), pointing at this subdirectory:

```lua
{
  "radical-beard/evaluate",
  -- the plugin lives in a subfolder of the repo:
  config = function() require("evaluate").setup() end,
  -- lazy.nvim: set `rtp = "editors/nvim"` (or vendor editors/nvim as its own plugin).
}
```

Or drop `editors/nvim/` onto your `runtimepath` any way you like (packer, `:packadd`, a manual
`set rtp+=…`). No `setup()` call is required for filetype detection, highlighting, or
frontmatter IntelliSense — those activate from the runtimepath alone. `setup()` is only needed
to change defaults (e.g. turn the body LSP off, or point at a specific spec/server).

## Configuration

```lua
require("evaluate").setup({
  -- Directory (or file) holding the generated evaluate-api.json.
  -- nil => auto-detect by walking up to a downloads/spec/ (works in any consumer project).
  spec_dir = nil,

  -- `K`-style hover: frontmatter keys/apis get a doc float; body words defer to the Lua LSP.
  -- Set to false to keep your own K mapping.
  hover_key = "K",

  lua_ls = {
    enable = true,                       -- attach lua-language-server to the Lua body
    cmd = { "lua-language-server" },     -- override if it isn't on PATH
    library = nil,                       -- extra LuaCATS/library dirs to load
  },
})
```

## Completion

Completion is exposed through the buffer's `omnifunc`, so it works with built-in
`<C-x><C-o>` and with any completion engine that sources `omnifunc`:

- **nvim-cmp** — add the `omni` source (`cmp-omni`) for the `evt` filetype.
- **blink.cmp** — enable its omnifunc/`complete_func` provider for `evt`.

## Notes

- The frontmatter tooling is a Lua port of the VS Code extension's; the two stay at parity.
- The body LSP uses lua-language-server's own settings (Lua 5.2 runtime, the generated LuaCATS
  as `workspace.library`, `self`/`config` as known globals, `lowercase-global` silenced). Your
  global lua_ls config is untouched — this is a separate client scoped to `.evt` buffers.
- Highlighting uses a classic Vim `syntax` file (`:syntax include`), which needs no Tree-sitter
  grammar. If you maintain a combined `evt` Tree-sitter setup, injections can replace it later.
