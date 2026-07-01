-- Loads the slice of the generated `evaluate-api.json` the frontmatter tooling needs
-- (apis / hooks / keys / grammars), plus the LuaCATS library dirs for the Lua body LSP.
-- When no spec is found it falls back to a small bundled copy of the stable names — the
-- live spec always wins, so the tooling tracks the consumer's exact Godot + Evaluate.

local M = {}

-- Stable fallback (mirrors editors/vscode/src/spec.ts BUNDLED). The live spec wins.
local BUNDLED = {
  apis = { "input", "world", "scene", "save", "sql" },
  apiMembers = {
    input = { "is_down" },
    world = {},
    scene = { "add", "change", "current", "find" },
    save = { "delete", "get", "set" },
    sql = { "exec", "exec_async", "flush", "query", "query_row", "snapshot", "transaction" },
  },
  apiNotes = {
    world = "The persistent global-root Node (a wrapped instance, not a table). Survives scene switches.",
  },
  systemHooks = {
    "on_start", "on_load", "on_unload", "on_quit", "on_enter", "on_exit",
    "on_focus_in", "on_focus_out", "on_pause", "on_resume",
    "on_update", "on_physics_update", "on_input",
  },
  nodeHooks = {
    "on_attach", "on_load", "on_unload", "on_update", "on_physics_update",
    "on_input", "on_exit", "on_quit", "on_focus_in", "on_focus_out", "on_pause", "on_resume",
  },
  frontmatterKeys = { "config", "apis", "register", "returns", "params", "require", "assets", "scenes" },
  returnsGrammar = "<name>  |  <name>: 'get set <type>'  (read-only omits 'set')",
  paramsGrammar = "<name>: <type> | <name>: <default> | <name>: '<type> = <default>'",
  requireGrammar = "<name>: \"<path.evt>\"  (a list of single-key maps or a map)",
}

-- Hover docs for the frontmatter keys (hand-authored — mirrors editors/vscode/src/frontmatter.ts).
M.KEY_DOCS = {
  config = "TOML files exposed as `config.<section>.*` (read live; hot-reloads).",
  apis = "Capability apis added to the sandbox. Only what you list here is reachable.",
  register = "Lifecycle hooks this script handles — each needs a matching global `function`.",
  returns = "The narrowed module contract for `require` consumers.",
  params = "Node scripts only: typed per-instance values the scene supplies via `params = {..}`, "
    .. "read through the `params` global. `name: <type>` (required) / `name: <default>` / "
    .. "`name: '<type> = <default>'`. Types: number/string/bool/list/table/any.",
  require = "Bind modules to sandbox locals up front — `name: \"path/to/module.evt\"` — so the "
    .. "body uses `name` with no `local name = require(...)` line. Each binds the same "
    .. "returns-narrowed handle `require(path)` would. Accepts a list of `- name: \"path\"` "
    .. "or a `params:`-style map.",
  assets = "Files watched for hot-reload alongside the script.",
  scenes = "System scripts only: restrict hooks to these scenes (omit ⇒ global).",
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
      spec.systemHooks = (raw.hooks and raw.hooks.system) or spec.systemHooks
      spec.nodeHooks = (raw.hooks and raw.hooks.node) or spec.nodeHooks
      if raw.frontmatter then
        spec.frontmatterKeys = raw.frontmatter.keys or spec.frontmatterKeys
        spec.returnsGrammar = raw.frontmatter.returnsGrammar or spec.returnsGrammar
        spec.paramsGrammar = raw.frontmatter.paramsGrammar or spec.paramsGrammar
        spec.requireGrammar = raw.frontmatter.requireGrammar or spec.requireGrammar
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
