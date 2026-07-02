-- `.evt`, `.node.evt`, `.behavior.evt` and `.statemachine.evt` are all Evaluate scripts.
-- Extension matching keys on the FINAL extension, so every compound form maps to the `evt`
-- filetype through this one entry (the plugin distinguishes node-attached scripts by the
-- `.node.evt` / `.behavior.evt` / `.statemachine.evt` filename).
vim.filetype.add({ extension = { evt = "evt" } })
