-- Evaluate.nvim — IntelliSense for `.evt` scripts (YAML frontmatter + sandboxed Lua body).
--
-- Filetype detection, embedded highlighting, and frontmatter IntelliSense work with the
-- plugin merely on the runtimepath (no setup call needed). `setup{}` only adjusts config and
-- is the place to turn the Lua-body LSP off or point at a specific spec/server.

local M = {}

M.config = {
  -- Directory (or file) holding the generated `evaluate-api.json`. nil => auto-detect by
  -- walking up from the file to a `downloads/spec/` (works inside any consumer project).
  spec_dir = nil,
  -- `K`-style key for hover: frontmatter keys/apis get a doc float; body words defer to the
  -- Lua LSP's hover. Set to false/"" to leave your own keymaps alone.
  hover_key = "K",
  lua_ls = {
    enable = true, -- attach lua-language-server to the Lua body (needs the binary on PATH)
    cmd = { "lua-language-server" },
    library = nil, -- extra LuaCATS/library dirs, in addition to the generated one
  },
}

function M.setup(opts)
  M.config = vim.tbl_deep_extend("force", M.config, opts or {})
  require("evaluate.spec").clear_cache()
end

-- Called from ftplugin/evt.lua for each `.evt` buffer.
function M.on_ft(bufnr)
  bufnr = bufnr or vim.api.nvim_get_current_buf()
  require("evaluate.frontmatter").attach(bufnr, M.config)
  if M.config.lua_ls and M.config.lua_ls.enable then
    require("evaluate.lua_ls").attach(bufnr, M.config)
  end
end

return M
