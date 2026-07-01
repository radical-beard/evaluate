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

The plugin lives in the **`editors/nvim/` subfolder** of the repo, which most plugin managers
don't expose as a runtimepath by default — so add that subfolder to `runtimepath` yourself.

With [lazy.nvim](https://github.com/folke/lazy.nvim):

```lua
{
  "radical-beard/evaluate",
  branch = "main",
  lazy = false, -- a filetype plugin: register `.evt` detection at startup
  dependencies = { "hrsh7th/cmp-omni" }, -- optional: frontmatter completion in nvim-cmp
  config = function()
    -- lazy clones the repo to <lazy-root>/evaluate; the plugin is under editors/nvim.
    vim.opt.rtp:append(vim.fn.stdpath("data") .. "/lazy/evaluate/editors/nvim")
    vim.filetype.add({ extension = { evt = "evt" } })
    require("evaluate").setup()

    -- optional: surface frontmatter completion (omnifunc) in nvim-cmp for `.evt`
    local ok, cmp = pcall(require, "cmp")
    if ok then
      cmp.setup.filetype("evt", {
        sources = cmp.config.sources({
          { name = "omni" },      -- frontmatter keys/apis/hooks
          { name = "nvim_lsp" },  -- Lua body (the attached lua-language-server)
          { name = "path" },
        }),
      })
    end
  end,
}
```

Without nvim-cmp, frontmatter completion is still available via built-in omni-completion
(`<C-x><C-o>`), and body completion/hover/diagnostics come from the attached
`lua-language-server`. `setup()` only adjusts defaults (turn the body LSP off, point at a
specific spec/server); filetype detection, highlighting and frontmatter IntelliSense need only
the `editors/nvim/` dir on `runtimepath`.

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
