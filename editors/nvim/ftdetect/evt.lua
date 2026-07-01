-- `.evt` and `.node.evt` are Evaluate scripts. Extension matching keys on the final
-- extension, so both map to the `evt` filetype (the plugin distinguishes node scripts by
-- the `.node.evt` filename).
vim.filetype.add({ extension = { evt = "evt" } })
