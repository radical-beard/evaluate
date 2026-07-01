-- Buffer-local setup for `.evt` scripts. Runs whenever a buffer's filetype becomes `evt`.
if vim.b.did_ftplugin_evt then return end
vim.b.did_ftplugin_evt = true

local bufnr = vim.api.nvim_get_current_buf()

-- The body is Lua; the frontmatter is YAML. Lua's line comment is the sensible default.
vim.bo[bufnr].commentstring = "-- %s"
vim.bo[bufnr].comments = ":--"

-- Wire frontmatter IntelliSense (+ the Lua body LSP, if enabled). Guarded so a broken
-- config never blocks opening the file.
local ok, err = pcall(function()
  require("evaluate").on_ft(bufnr)
end)
if not ok then
  vim.notify("evaluate.nvim: " .. tostring(err), vim.log.levels.WARN)
end
