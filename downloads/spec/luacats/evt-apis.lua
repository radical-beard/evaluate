---@meta
-- EvaLuate capability apis. A script reaches one only after declaring it in
-- frontmatter `apis:` (godot/std are ambient). Lifecycle hooks & frontmatter keys
-- are listed at the bottom for reference.

---@class evt.input
---@field is_down fun(...): any
input = {}

---@class evt.save
---@field delete fun(...): any
---@field get fun(...): any
---@field set fun(...): any
save = {}

---@class evt.scene
---@field add fun(...): any
---@field change fun(...): any
---@field current fun(...): any
---@field find fun(...): any
scene = {}

---@class evt.sql
---@field exec fun(...): any
---@field exec_async fun(...): any
---@field flush fun(...): any
---@field query fun(...): any
---@field query_row fun(...): any
---@field snapshot fun(...): any
---@field transaction fun(...): any
sql = {}

-- world: The persistent global-root Node (a wrapped godot instance, not a table). Use any Node method/property — e.g. world:add_child(node). Survives scene switches.
-- system hooks (register: on a .evt):   on_start, on_load, on_unload, on_quit, on_enter, on_exit, on_focus_in, on_focus_out, on_pause, on_resume, on_update, on_physics_update, on_input
-- node hooks   (register: on a .node.evt): on_attach, on_load, on_unload, on_update, on_physics_update, on_input, on_exit, on_quit, on_focus_in, on_focus_out, on_pause, on_resume
-- frontmatter keys: config, apis, register, returns, params, assets, scenes
-- returns grammar: <name>  |  <name>: 'get set <type>'  (read-only omits 'set')
-- params grammar:  <name>: <type> | <name>: <default> | <name>: '<type> = <default>'  (node scripts only; types: number/string/bool/list/table/any; no default = required; the scene supplies values via `params = {..}` on the node)
