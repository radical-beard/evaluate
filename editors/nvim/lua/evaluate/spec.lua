-- Loads the slice of the generated `evaluate-api.json` the frontmatter tooling needs
-- (apis / hooks / keys / grammars), plus the LuaCATS library dirs for the Lua body LSP.
-- When no spec is found it falls back to a small bundled copy of the stable names — the
-- live spec always wins, so the tooling tracks the consumer's exact Godot + Evaluate.

local M = {}

-- Stable fallback (mirrors editors/vscode/src/spec.ts BUNDLED). The live spec wins.
local BUNDLED = {
  -- The framework services. Since 0.10.0, `apis:` ALSO accepts any Godot class or
  -- global-enum name (PascalCase — `apis: [Node3D, Side]` injects each as a bare
  -- global) and host-registered C# extension apis; those aren't enumerable here, so
  -- validation accepts PascalCase entries and only the blocked names below are errors.
  -- 0.11.0: raw input is native — the `input` service and `on_input` hook are gone;
  -- input classes (Input, Key, InputEvent*, ...) are blocked. Use `actions`/`controller`.
  apis = { "world", "scene", "save", "sql", "actions", "controller", "store" },
  apiMembers = {
    world = {},
    scene = { "add", "change", "context", "current", "find", "list", "pop", "push", "stack" },
    save = { "delete", "get", "set" },
    sql = { "exec", "exec_async", "flush", "query", "query_row", "snapshot", "transaction" },
    actions = {},
    controller = {
      "capture_text", "joy_name", "overrides", "possess", "possessed", "release",
      "rebind", "reset_overrides", "rumble", "scenario",
    },
    store = { "delete", "get", "has", "keys", "set", "subscribe" },
  },
  apiNotes = {
    world = "The persistent global-root Node (a wrapped instance, not a table). Survives scene switches.",
    actions = "actions.<Scenario>.<Action> — mapped input from the manifest's controls TOML. "
      .. "Each action exposes subscribe{ on = \"press|release|tap|held\", after = seconds, run = fn } "
      .. "plus live `down`/`value`/`vector` reads. Unknown names are load errors.",
    controller = "The native PlayerController: scenario routing, possession, save-DB rebinds, rumble, text capture.",
    store = "Global SESSION state (survives scene switches; never written to disk — that's save/sql).",
  },
  -- Never declarable in `apis:` (assets load via frontmatter `assets:`; raw input is
  -- native-only — InputEvent* is prefix-blocked by the loader).
  blockedApis = {
    "DirAccess", "FileAccess", "ResourceLoader", "ResourceSaver",
    "Input", "InputMap", "Key", "KeyModifierMask", "JoyButton", "JoyAxis",
    "MouseButton", "MouseButtonMask",
  },
  -- Godot class / global-enum names come from the live spec (empty when bundled); they only
  -- power completion/hover — validation accepts any PascalCase name without a list.
  godotClasses = {},
  godotEnums = {},
  systemHooks = {
    "on_start", "on_load", "on_unload", "on_quit", "on_enter", "on_exit",
    "on_focus_in", "on_focus_out", "on_pause", "on_resume",
    "on_update", "on_physics_update",
  },
  nodeHooks = {
    "on_attach", "on_load", "on_unload", "on_update", "on_physics_update",
    "on_exit", "on_quit", "on_focus_in", "on_focus_out", "on_pause", "on_resume",
  },
  frontmatterKeys = {
    "config", "apis", "register", "returns", "params", "require", "assets", "scenes",
    "properties", "behaviors", "machines", "attributes", "abilities",
    "name", "states", "initial", -- *.statemachine.evt signature keys
  },
  returnsGrammar = "<name>  |  <name>: 'get set <type>'  (read-only omits 'set')",
  paramsGrammar = "<name>: <type> | <name>: <default> | <name>: '<type> = <default>'  (types: number/string/bool/list/table/any/dna)",
  requireGrammar = "<name>: \"<path.evt>\"  (a list of single-key maps or a map)",
  assetsGrammar = "<name>: \"<res-relative path>\"  (map or list of single-key maps; injected as assets.<name>)",
  propertiesGrammar = "<property>: <value>  (node-attached scripts only; applied to self at attach + reload)",
  attributesGrammar = "<name>: { base: n, min: n, max: n, regen: n/s, regen_delay: s, recover: n }",
  machineGrammar = "*.statemachine.evt: frontmatter name:/states:/initial:; body returns ordered transitions",
}

-- Hover docs for the frontmatter keys (hand-authored — mirrors editors/vscode/src/frontmatter.ts).
M.KEY_DOCS = {
  config = "TOML files exposed as `config.<section>.*` (read live; hot-reloads).",
  apis = "Everything the sandbox exposes, each injected as its own bare global: framework "
    .. "services (`input`, `world`, `scene`, `save`, `sql`), host-registered extension apis, "
    .. "and Godot classes/enums (`apis: [Input, Node3D, Key]` → `Input.GetJoyAxis(...)`, "
    .. "`Node3D.new()`, `Key.Space`). The ambient `godot.*` table is gone (0.10.0) and "
    .. "`godot:`-prefixed entries are a load error. Blocked: `DirAccess`, `FileAccess`, "
    .. "`ResourceLoader`, `ResourceSaver` — assets load via frontmatter `assets:`.",
  register = "Lifecycle hooks this script handles — each needs a matching global `function`.",
  returns = "The narrowed module contract for `require` consumers.",
  params = "Node-attached scripts only: typed per-instance values the scene supplies via "
    .. "`params = {..}`, read through the `params` global. `name: <type>` (required) / "
    .. "`name: <default>` / `name: '<type> = <default>'`. Types: number/string/bool/list/"
    .. "table/any/dna (dna = `\"0x\"` + exactly 16 hex digits; read `params.<name>:trait(1..16)` "
    .. "and `:hex()`).",
  require = "Bind modules to sandbox locals up front — `name: \"path/to/module.evt\"` — so the "
    .. "body uses `name` with no `local name = require(...)` line. Each binds the same "
    .. "returns-narrowed handle `require(path)` would. Accepts a list of `- name: \"path\"` "
    .. "or a `params:`-style map.",
  assets = "Map of `<name>: \"res-relative/path.ext\"` (or a list of single-key maps) — the ONLY "
    .. "way a script gets an asset. Loaded eagerly, injected as the ambient `assets` table "
    .. "(`assets.<name>`), hot-reloaded; a filename `*` glob binds a stem-keyed table; "
    .. "`.gdshader` updates in place. Bare-path list entries are an error.",
  scenes = "System scripts only: restrict hooks to these scenes (omit ⇒ global).",
  properties = "Node-attached scripts only: map of native Godot property → value, applied to "
    .. "`self` at attach and on script reload (scene-set keys win). Same value grammar as "
    .. "`.scene` properties, incl. `[x, y, z]` lists and `_type` tables.",
  behaviors = "List of `*.behavior.evt` paths composed onto the same node — each runs against "
    .. "the shared `self` (`.node.evt` is the deprecated single-behavior alias).",
  machines = "List of `*.statemachine.evt` paths attached to the node. Each surfaces "
    .. "`self.fsm.<name>` — `.state`, `:is(s)`, `:fire(evt)`, `:on_exit(state, fn)`, and "
    .. "`self.fsm.<name>.<state> = fn` appends an enter listener `fn(from)`.",
  attributes = "Node-attached: map of `<name>: { base, min, max, regen, regen_delay, recover }`. "
    .. "Read `self.attributes.<name>`; spent by abilities; exhausts at min until it recovers.",
  abilities = "List of `*.ability` files (TOML) granted to the node at attach.",
  name = "State machines (`*.statemachine.evt`): the machine's name — surfaced as `self.fsm.<name>`.",
  states = "State machines: the list of state names. The body returns ordered transitions "
    .. "`{ from=, to=, when=fn|on=\"event\"|after=seconds [, run=fn] }`.",
  initial = "State machines: the starting state (one of `states:`).",
}

local function exists(p)
  return p ~= nil and p ~= "" and (vim.uv or vim.loop).fs_stat(p) ~= nil
end

-- Find `downloads/spec/evaluate-api.json`: an explicit dir first, then by walking up from
-- `start` (the file's directory) looking for the generated spec, so it works from anywhere
-- inside a consumer project without configuration.
local function find_spec_file(start, configured)
  if configured and configured ~= "" then
    local direct = configured
    if not direct:match("%.json$") then direct = direct .. "/evaluate-api.json" end
    if exists(direct) then return direct end
  end
  local found = vim.fs.find("evaluate-api.json", {
    upward = true,
    path = start,
    type = "file",
    -- only accept it when it sits under a downloads/spec (the generated location)
    stop = vim.uv and vim.uv.os_homedir() or nil,
  })
  for _, f in ipairs(found or {}) do
    if f:match("downloads/spec/evaluate%-api%.json$") or f:match("/spec/evaluate%-api%.json$") then
      return f
    end
  end
  -- Fallback: any evaluate-api.json upward.
  return (found or {})[1]
end

-- The LuaCATS library dirs a lua-language-server should load for a resolved spec file.
function M.luacats_dirs(spec_file)
  if not spec_file then return {} end
  local dir = vim.fs.dirname(spec_file) .. "/luacats"
  return exists(dir) and { dir } or {}
end

local cache = {}

-- Load the spec for the file in `bufnr`. `config.spec_dir` overrides discovery.
function M.load(bufnr, config)
  local name = vim.api.nvim_buf_get_name(bufnr or 0)
  local start = name ~= "" and vim.fs.dirname(name) or vim.uv.cwd()
  local file = find_spec_file(start, config and config.spec_dir)

  if file and cache[file] then return cache[file] end

  local spec = vim.tbl_extend("force", { source = "bundled" }, BUNDLED)
  spec.specFile = file

  if file and exists(file) then
    local ok, raw = pcall(function()
      return vim.json.decode(table.concat(vim.fn.readfile(file), "\n"))
    end)
    if ok and type(raw) == "table" then
      local apis, apiMembers, apiNotes = {}, {}, {}
      for _, a in ipairs(raw.apis or {}) do
        table.insert(apis, a.name)
        apiMembers[a.name] = a.members or {}
        if a.note then apiNotes[a.name] = a.note end
      end
      if #apis > 0 then
        spec.apis, spec.apiMembers = apis, apiMembers
        if next(apiNotes) then spec.apiNotes = apiNotes end
      end
      spec.blockedApis = raw.blockedApis or spec.blockedApis
      -- 0.10.0 apis-as-modules: Godot classes/global enums are declarable in `apis:` too.
      if raw.godot then
        local function names(list)
          local out = {}
          for _, e in ipairs(list or {}) do
            if type(e) == "table" and type(e.name) == "string" then table.insert(out, e.name) end
          end
          return out
        end
        spec.godotClasses = names(raw.godot.classes)
        spec.godotEnums = names(raw.godot.globalEnums)
      end
      spec.systemHooks = (raw.hooks and raw.hooks.system) or spec.systemHooks
      spec.nodeHooks = (raw.hooks and raw.hooks.node) or spec.nodeHooks
      if raw.frontmatter then
        spec.frontmatterKeys = raw.frontmatter.keys or spec.frontmatterKeys
        spec.returnsGrammar = raw.frontmatter.returnsGrammar or spec.returnsGrammar
        spec.paramsGrammar = raw.frontmatter.paramsGrammar or spec.paramsGrammar
        spec.requireGrammar = raw.frontmatter.requireGrammar or spec.requireGrammar
        spec.assetsGrammar = raw.frontmatter.assetsGrammar or spec.assetsGrammar
        spec.propertiesGrammar = raw.frontmatter.propertiesGrammar or spec.propertiesGrammar
        spec.attributesGrammar = raw.frontmatter.attributesGrammar or spec.attributesGrammar
        spec.machineGrammar = raw.frontmatter.machineGrammar or spec.machineGrammar
      end
      spec.evaluateVersion, spec.godotVersion = raw.evaluateVersion, raw.godotVersion
      spec.source = "workspace"
      cache[file] = spec
    end
  end

  return spec
end

function M.clear_cache() cache = {} end

return M
