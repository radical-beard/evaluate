---@meta
-- EvaLuate capability apis. A script reaches one only after declaring it in
-- frontmatter `apis:` (only `std` is ambient; Godot classes are declared the
-- same way and injected as bare globals — see godot-core/full.lua). Lifecycle
-- hooks & frontmatter keys are listed at the bottom for reference.

-- actions: actions.<Scenario>.<Action> — mapped input, resolved against the manifest's controls TOML (unknown names are load errors). Each action exposes subscribe{ on = "press|release|tap|held", after = seconds, run = fn } plus live `down` (bool), `value` (0..1), and `vector` ({x, y}) reads. Raw input classes (Input, Key, InputEvent*, ...) are not declarable.
---@class evt.controller
---@field capture_text fun(...): any
---@field joy_name fun(...): any
---@field overrides fun(...): any
---@field possess fun(...): any
---@field possessed fun(...): any
---@field rebind fun(...): any
---@field release fun(...): any
---@field reset_overrides fun(...): any
---@field rumble fun(...): any
---@field scenario fun(...): any
controller = {}

---@class evt.save
---@field delete fun(...): any
---@field get fun(...): any
---@field set fun(...): any
save = {}

---@class evt.scene
---@field add fun(...): any
---@field change fun(...): any
---@field context fun(...): any
---@field current fun(...): any
---@field find fun(...): any
---@field list fun(...): any
---@field pop fun(...): any
---@field push fun(...): any
---@field stack fun(...): any
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

---@class evt.store
---@field delete fun(...): any
---@field get fun(...): any
---@field has fun(...): any
---@field keys fun(...): any
---@field set fun(...): any
---@field subscribe fun(...): any
store = {}

-- world: The persistent global-root Node (a wrapped godot instance, not a table). Use any Node method/property — e.g. world:add_child(node). Survives scene switches.
-- system hooks (register: on a .evt):   on_start, on_load, on_unload, on_quit, on_enter, on_exit, on_focus_in, on_focus_out, on_pause, on_resume, on_update, on_physics_update
-- node hooks   (register: on a .node.evt / .behavior.evt): on_attach, on_load, on_unload, on_update, on_physics_update, on_exit, on_quit, on_focus_in, on_focus_out, on_pause, on_resume
-- frontmatter keys: config, apis, register, returns, params, require, assets, scenes, properties, behaviors, machines, attributes, abilities, name, states, initial
-- returns grammar: <name>  |  <name>: 'get set <type>'  (read-only omits 'set')
-- params grammar:  <name>: <type> | <name>: <default> | <name>: '<type> = <default>'  (node-attached scripts only; types: number/string/bool/list/table/any/dna; no default = required; the scene supplies values via `params = {..}`; dna = "0x" + exactly 16 hex digits, read as params.<name>:trait(1..16) / :hex())
-- require grammar: <name>: "<path.evt>"  (a list of single-key maps or a map; each binds the returns-narrowed module at <path> — resolved relative to the scripts root — to a sandbox local <name>, so the body uses it with no `local <name> = require(...)` line)
-- assets grammar:  <name>: "<res-relative path>"  (map or list of single-key maps; the ONLY way a script gets an asset — loaded eagerly, injected as `assets.<name>`, hot-reloaded; a filename `*` glob binds a stem-keyed table; `.gdshader` updates in place)
-- properties grammar: <property>: <value>  (node-attached scripts only; native Godot properties applied to self at attach + on script reload; scene-set keys win; same value grammar as .scene properties incl. [x, y, z] lists and _type tables)
-- attributes grammar: <name>: { base: n, min: n, max: n, regen: n/s, regen_delay: s, recover: n }  (node-attached; read self.attributes.<name>, spend via abilities; exhaust at min until recover; `abilities:` lists *.ability files granted at attach)
-- statemachines: *.statemachine.evt: frontmatter name:/states:/initial:; body returns ordered transitions { from=, to=, when=fn|on="event"|after=s [, run=fn] }; surface self.fsm.<name> — .state, :is(s), :fire(evt), :on_exit(s, fn), and `self.fsm.<name>.<state> = fn` appends an enter listener fn(from)
-- blocked apis (never declarable): DirAccess, FileAccess, ResourceLoader, ResourceSaver
