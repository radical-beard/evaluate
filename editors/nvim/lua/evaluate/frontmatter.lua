-- Frontmatter IntelliSense for `.evt` buffers: completion (omnifunc), diagnostics, and
-- hover — a Lua port of editors/vscode/src/frontmatter.ts. Reads the spec loaded by
-- evaluate/spec.lua (generated `evaluate-api.json`, with a bundled fallback), so the
-- keys/apis/hooks it offers and validates track the exact Godot + Evaluate in the project.

local spec_mod = require("evaluate.spec")

local M = {}

local NS = vim.api.nvim_create_namespace("evaluate.frontmatter")
local configs = {} -- bufnr -> resolved plugin config (for hover/lua_ls handoff)

-- ---- parsing (1-indexed lines, matching nvim_buf_get_lines rows +1) --------

-- The leading `--- … ---` block as { s = <opening row>, e = <closing row> } (1-indexed),
-- or nil when the file has no signature / an unterminated block.
local function fm_range(lines)
  if #lines == 0 or vim.trim(lines[1]) ~= "---" then return nil end
  for i = 2, #lines do
    if vim.trim(lines[i]) == "---" then return { s = 1, e = i } end
  end
  return nil
end

-- The nearest top-level `key:` at or above row `row` (the section being edited under).
local function section_at(lines, fm, row)
  for i = row, fm.s + 1, -1 do
    local k = lines[i]:match("^([%a_]+):")
    if k then return k end
  end
  return nil
end

-- Values already listed under `section` (so completion can skip them).
local function listed_under(lines, fm, section)
  local out, in_section = {}, false
  for i = fm.s + 1, fm.e - 1 do
    local key = lines[i]:match("^([%a_]+):")
    if key then
      in_section = key == section
    elseif in_section then
      local item = lines[i]:match("^%s*%-%s*([^:#%s]+)")
      if item then out[item] = true end
    end
  end
  return out
end

-- Node-attached scripts (`self`-bound, node hooks): `*.behavior.evt` behaviors (`.node.evt`
-- is its deprecated alias) and `*.statemachine.evt` state machines.
local function is_node_script(bufnr)
  local name = vim.api.nvim_buf_get_name(bufnr)
  return name:match("%.node%.evt$") ~= nil
    or name:match("%.behavior%.evt$") ~= nil
    or name:match("%.statemachine%.evt$") ~= nil
end

local function hooks_for(bufnr, spec)
  return is_node_script(bufnr) and spec.nodeHooks or spec.systemHooks
end

-- The capability apis this file declared in `apis:` (what the sandbox actually exposes).
local function declared_apis(lines, fm)
  return fm and listed_under(lines, fm, "apis") or {}
end

-- ---- completion (omnifunc) -------------------------------------------------

-- omnifunc protocol: (1, "") -> start column; (0, base) -> candidate list.
function M.omnifunc(findstart, base)
  local bufnr = vim.api.nvim_get_current_buf()
  local pos = vim.api.nvim_win_get_cursor(0)
  local row, col = pos[1], pos[2] -- row 1-indexed, col 0-indexed (byte before cursor)
  local line = vim.api.nvim_get_current_line()

  if findstart == 1 then
    local start = col
    while start > 0 and line:sub(start, start):match("[%w_]") do
      start = start - 1
    end
    M._start = start
    return start
  end

  local lines = vim.api.nvim_buf_get_lines(bufnr, 0, -1, false)
  local fm = fm_range(lines)
  if not fm or not (row > fm.s and row < fm.e) then return {} end

  local spec = spec_mod.load(bufnr, configs[bufnr])
  local prefix = line:sub(1, col)
  local items = {}

  -- A list item under a section -> that section's values.
  if prefix:match("^%s*%-%s*%S*$") then
    local section = section_at(lines, fm, row)
    if section == "apis" then
      local used = listed_under(lines, fm, "apis")
      for _, a in ipairs(spec.apis) do
        if not used[a] then
          local members = spec.apiMembers[a] or {}
          local info = spec.apiNotes[a]
            or ("Members: " .. (#members > 0 and (a .. "." .. table.concat(members, ", " .. a .. ".")) or "(node)"))
          table.insert(items, { word = a, kind = "api", menu = "capability", info = info })
        end
      end
      -- 0.10.0 apis-as-modules: any Godot class/enum is declarable too, each injected as a
      -- bare global (only known when the live spec is loaded).
      local blocked = {}
      for _, b in ipairs(spec.blockedApis or {}) do blocked[b] = true end
      for _, cls in ipairs(spec.godotClasses or {}) do
        if not used[cls] and not blocked[cls] then
          table.insert(items, { word = cls, kind = "class", menu = "godot", info = "Godot class (injected as a bare global)" })
        end
      end
      for _, en in ipairs(spec.godotEnums or {}) do
        if not used[en] then
          table.insert(items, { word = en, kind = "enum", menu = "godot", info = "Godot global enum (injected as a bare global)" })
        end
      end
    elseif section == "register" then
      local used = listed_under(lines, fm, "register")
      local kind = is_node_script(bufnr) and "node hook" or "system hook"
      for _, h in ipairs(hooks_for(bufnr, spec)) do
        if not used[h] then table.insert(items, { word = h, kind = "hook", menu = kind }) end
      end
    end
    -- config/scenes/assets/returns/require: free text (no suggestions)
    return M._filter(items, base)
  end

  -- Start of a top-level line -> frontmatter keys.
  if prefix:match("^%s*[%a_]*$") and not prefix:match("^%s+") then
    local present = {}
    for i = fm.s + 1, fm.e - 1 do
      local k = lines[i]:match("^([%a_]+):")
      if k then present[k] = true end
    end
    for _, k in ipairs(spec.frontmatterKeys) do
      if not present[k] then
        table.insert(items, { word = k, kind = "key", menu = "frontmatter", info = spec_mod.KEY_DOCS[k] })
      end
    end
  end

  return M._filter(items, base)
end

function M._filter(items, base)
  if not base or base == "" then return items end
  local out = {}
  for _, it in ipairs(items) do
    if it.word:sub(1, #base) == base then table.insert(out, it) end
  end
  return out
end

-- ---- diagnostics -----------------------------------------------------------

local function escape(s)
  return (s:gsub("[%(%)%.%%%+%-%*%?%[%]%^%$]", "%%%1"))
end

-- Standalone-identifier byte positions (1-based start) of `w` in `line` — not preceded by a
-- word char / `.` / `:` and not followed by a word char.
local function ident_positions(line, w)
  local out, init, ew = {}, 1, escape(w)
  while true do
    local s, e = line:find(ew, init, true)
    if not s then break end
    local before = s > 1 and line:sub(s - 1, s - 1) or ""
    local after = line:sub(e + 1, e + 1)
    if (before == "" or not before:match("[%w_.:]")) and not after:match("[%w_]") then
      table.insert(out, s)
    end
    init = e + 1
  end
  return out
end

-- The next non-space char at/after byte index `i`.
local function next_token(line, i)
  local rest = line:sub(i):match("^%s*(.)")
  return rest or ""
end

-- Body references to a capability api the frontmatter did not declare. The sandbox only
-- exposes declared apis, so such a use is nil at runtime — flag it. A name the body locally
-- rebinds (`local save` / `save = …`) shadows the absent global, so it is excluded.
local function undeclared_api_uses(lines, body_start, all_apis, declared)
  local code = {}
  for i = 1, #lines do
    code[i] = i > body_start and lines[i]:gsub("%-%-.*$", "") or ""
  end
  local body = table.concat(code, "\n")

  local undeclared = {}
  for _, a in ipairs(all_apis) do
    if not declared[a] then
      local rebound = body:find("%f[%w_]local%s+" .. escape(a) .. "%f[^%w_]") ~= nil
      if not rebound then
        for _, s in ipairs(ident_positions(body, a)) do
          local nt = next_token(body, s + #a)
          if nt == "=" and body:sub(s + #a):match("^%s*=%s*[^=]") then
            rebound = true
            break
          end
        end
      end
      if not rebound then undeclared[a] = true end
    end
  end

  local diags = {}
  for i = body_start + 1, #lines do
    for a in pairs(undeclared) do
      for _, s in ipairs(ident_positions(code[i], a)) do
        local nt = next_token(code[i], s + #a)
        if nt == "." or nt == ":" or nt == "(" then
          table.insert(diags, {
            lnum = i - 1,
            col = s - 1,
            end_col = s - 1 + #a,
            severity = vim.diagnostic.severity.WARN,
            source = "evt",
            message = ("'%s' is used but not declared in 'apis:'. The sandbox only exposes declared "
              .. "apis, so this is nil at runtime. Add '%s' to 'apis:'."):format(a, a),
          })
        end
      end
    end
  end
  return diags
end

function M.validate(bufnr)
  if vim.bo[bufnr].filetype ~= "evt" then return end
  local lines = vim.api.nvim_buf_get_lines(bufnr, 0, -1, false)
  local fm = fm_range(lines)
  if not fm then
    vim.diagnostic.reset(NS, bufnr)
    return
  end

  local spec = spec_mod.load(bufnr, configs[bufnr])
  local valid_hooks = {}
  for _, h in ipairs(hooks_for(bufnr, spec)) do valid_hooks[h] = true end
  local valid_apis = {}
  for _, a in ipairs(spec.apis) do valid_apis[a] = true end
  local valid_keys = {}
  for _, k in ipairs(spec.frontmatterKeys) do valid_keys[k] = true end
  local blocked_apis = {}
  for _, b in ipairs(spec.blockedApis or {}) do blocked_apis[b] = true end

  local diags, section = {}, nil
  for i = fm.s + 1, fm.e - 1 do
    local text = lines[i]
    local key = text:match("^([%a_]+):")
    if key then
      section = key
      if not valid_keys[section] then
        table.insert(diags, {
          lnum = i - 1, col = 0, end_col = #key, severity = vim.diagnostic.severity.WARN, source = "evt",
          message = ("Unknown frontmatter key '%s'. Valid: %s."):format(section, table.concat(spec.frontmatterKeys, ", ")),
        })
      end
    else
      local lead, value = text:match("^(%s*%-%s*)([^:#%s]+)")
      if value then
        local col = #lead
        if section == "apis" then
          -- The full token (incl. any ':'), to catch the removed `godot:Class` form.
          local entry = text:match("^%s*%-%s*(%S+)") or value
          if entry:match("^godot:") then
            local bare = entry:sub(#"godot:" + 1)
            table.insert(diags, {
              lnum = i - 1, col = col, end_col = col + #entry, severity = vim.diagnostic.severity.ERROR, source = "evt",
              message = ("'godot:'-prefixed entries were removed in 0.10.0 — declare the Godot class/enum "
                .. "name directly (e.g. '%s'); it is injected as a bare global."):format(bare ~= "" and bare or "Node3D"),
            })
          elseif blocked_apis[value] or value:match("^InputEvent") then
            local is_input = value:match("^Input") or value:match("^Key")
              or value:match("^Joy") or value:match("^Mouse")
            local why = is_input
              and "raw input is native-only — subscribe to mapped actions via the 'actions' api"
              or "assets load via frontmatter assets:; persistence via save/sql"
            table.insert(diags, {
              lnum = i - 1, col = col, end_col = col + #value, severity = vim.diagnostic.severity.ERROR, source = "evt",
              message = ("'%s' is blocked from declaration — %s."):format(value, why),
            })
          elseif not valid_apis[value] and not value:match("^%u") then
            -- PascalCase entries are Godot classes/enums (or host extension apis) — accepted.
            table.insert(diags, {
              lnum = i - 1, col = col, end_col = col + #value, severity = vim.diagnostic.severity.WARN, source = "evt",
              message = ("Unknown api '%s'. Framework services: %s; Godot classes/enums are declared by "
                .. "their PascalCase name. (It must be declared to be usable.)"):format(
                value, table.concat(spec.apis, ", ")),
            })
          end
        elseif section == "register" and not valid_hooks[value] then
          table.insert(diags, {
            lnum = i - 1, col = col, end_col = col + #value, severity = vim.diagnostic.severity.WARN, source = "evt",
            message = ("Unknown %s hook '%s'. Valid: %s."):format(
              is_node_script(bufnr) and "node" or "system", value, table.concat(hooks_for(bufnr, spec), ", ")),
          })
        elseif section == "assets" and not text:match("^%s*%-%s*[%a_][%w_]*%s*:") then
          -- 0.10.0: assets are a name -> path map (or list of single-key maps); bare paths error.
          table.insert(diags, {
            lnum = i - 1, col = col, end_col = col + #value, severity = vim.diagnostic.severity.ERROR, source = "evt",
            message = ("Bare-path 'assets:' entries are an error since 0.10.0 — name it ('<name>: \"%s\"'), "
              .. "injected as 'assets.<name>'."):format(value),
          })
        end
      end
    end
  end

  vim.list_extend(diags, undeclared_api_uses(lines, fm.e, spec.apis, declared_apis(lines, fm)))
  vim.diagnostic.set(NS, bufnr, diags)
end

-- ---- hover -----------------------------------------------------------------

-- Markdown lines describing the frontmatter word under the cursor, or nil (so a `K` mapping
-- can fall back to the Lua-body LSP for words in the body).
function M.hover_lines(bufnr)
  local lines = vim.api.nvim_buf_get_lines(bufnr, 0, -1, false)
  local fm = fm_range(lines)
  local pos = vim.api.nvim_win_get_cursor(0)
  local row = pos[1]
  if not fm or not (row > fm.s and row < fm.e) then return nil end

  local word = vim.fn.expand("<cword>")
  if word == "" then return nil end
  local spec = spec_mod.load(bufnr, configs[bufnr])

  local line = lines[row]
  if spec_mod.KEY_DOCS[word] and line:match("^[%a_]+:") then
    return { ("**%s** — %s"):format(word, spec_mod.KEY_DOCS[word]) }
  end
  if vim.tbl_contains(spec.apis, word) then
    local note = spec.apiNotes[word]
    local members = spec.apiMembers[word] or {}
    local detail = note
      or (#members > 0 and ("`" .. word .. "." .. table.concat(members, "`, `" .. word .. ".") .. "`") or "capability api")
    return { ("**%s** — %s"):format(word, detail) }
  end
  if vim.tbl_contains(spec.blockedApis or {}, word) then
    return { ("**%s** — blocked from `apis:` — assets load via frontmatter `assets:`."):format(word) }
  end
  if vim.tbl_contains(spec.godotClasses or {}, word) then
    return { ("**%s** — Godot class. Declared in `apis:`, injected as a bare global (`%s.new()`, statics, enums)."):format(word, word) }
  end
  if vim.tbl_contains(spec.godotEnums or {}, word) then
    return { ("**%s** — Godot global enum. Declared in `apis:`, injected as a bare global (`%s.<Constant>`)."):format(word, word) }
  end
  return nil
end

-- ---- attach ----------------------------------------------------------------

function M.attach(bufnr, config)
  configs[bufnr] = config
  vim.bo[bufnr].omnifunc = "v:lua.require'evaluate.frontmatter'.omnifunc"

  local group = vim.api.nvim_create_augroup("evaluate.frontmatter." .. bufnr, { clear = true })
  vim.api.nvim_create_autocmd({ "TextChanged", "TextChangedI", "InsertLeave", "BufWritePost", "BufEnter" }, {
    group = group,
    buffer = bufnr,
    callback = function() M.validate(bufnr) end,
  })
  vim.api.nvim_create_autocmd("BufUnload", {
    group = group,
    buffer = bufnr,
    callback = function()
      configs[bufnr] = nil
      vim.diagnostic.reset(NS, bufnr)
    end,
  })

  if config and config.hover_key and config.hover_key ~= "" then
    vim.keymap.set("n", config.hover_key, function()
      local hl = M.hover_lines(bufnr)
      if hl then
        vim.lsp.util.open_floating_preview(hl, "markdown", { border = "rounded", focus = false })
      else
        vim.lsp.buf.hover() -- body word: defer to the Lua LSP
      end
    end, { buffer = bufnr, desc = "Evaluate: hover (frontmatter or Lua body)" })
  end

  M.validate(bufnr)
end

return M
