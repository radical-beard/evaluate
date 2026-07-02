-- Lua-body IntelliSense for `.evt` via lua-language-server.
--
-- `.evt` = YAML frontmatter + Lua body. We attach lua_ls to the real `.evt` buffer but blank
-- the frontmatter in the text we send it — line-for-line, exactly what the runtime loader
-- does — so the Lua body keeps its exact line/columns and every position maps 1:1 (completion,
-- hover, diagnostics all come back keyed to the real buffer, no remapping). The blanking is
-- done by wrapping the client's `rpc.notify` (the single choke point every outgoing
-- notification passes through), the nvim analogue of the VS Code didOpen/didChange middleware.
-- The server is told the doc is Lua (languageId) and pointed at the generated LuaCATS library.

local spec_mod = require("evaluate.spec")

local M = {}

-- ---- frontmatter blanking (identity line mapping) --------------------------

-- 0-indexed row of the closing `---`, or -1 when there is no signature block.
local function fm_end_line(text)
  local lines = vim.split(text, "\n", { plain = true })
  if not lines[1] or vim.trim(lines[1]) ~= "---" then return -1 end
  for i = 2, #lines do
    if vim.trim(lines[i]) == "---" then return i - 1 end
  end
  return -1
end

local function blank(text)
  local endl = fm_end_line(text)
  if endl < 0 then return text end
  local lines = vim.split(text, "\n", { plain = true })
  for i = 1, endl + 1 do lines[i] = "" end -- rows 0..endl -> empty; positions unchanged
  return table.concat(lines, "\n")
end

-- ---- settings (mirrors editors/vscode/src/luaClient.ts luaSection) ----------

local function lua_settings(libdirs)
  return {
    Lua = {
      runtime = { version = "Lua 5.2" }, -- Evaluate's VM (Lua-CSharp, Lua 5.2)
      workspace = { library = libdirs, checkThirdParty = false },
      -- LuaCATS declare `std` plus the declarable apis (services and, since 0.10.0, Godot
      -- classes/enums as bare globals — no ambient `godot.*`); self/config/params/assets are
      -- contextual, and hook definitions are intentional "lowercase globals".
      diagnostics = { globals = { "self", "config", "params", "assets" }, disable = { "lowercase-global" } },
      telemetry = { enable = false },
    },
  }
end

-- ---- attach ----------------------------------------------------------------

function M.attach(bufnr, config)
  if vim.b[bufnr].evaluate_lua_attached then return end

  local cmd = (config and config.lua_ls and config.lua_ls.cmd) or { "lua-language-server" }
  if vim.fn.executable(cmd[1]) == 0 then
    return -- no server installed: frontmatter tooling still works; body LSP is opt-in
  end

  local spec = spec_mod.load(bufnr, config and config.spec_dir)
  local libdirs = spec_mod.luacats_dirs(spec.specFile)
  -- Let callers add extra library dirs (e.g. their own shared modules).
  if config and config.lua_ls and config.lua_ls.library then
    vim.list_extend(libdirs, config.lua_ls.library)
  end

  local name = vim.api.nvim_buf_get_name(bufnr)
  local root = vim.fs.root(bufnr, { "downloads", "Evaluate.slnx", "project.godot", ".git" })
    or (name ~= "" and vim.fs.dirname(name))
    or vim.uv.cwd()

  -- Advertise the completion capabilities a completion engine expects. Prefer an
  -- explicit override, else cmp_nvim_lsp's (so nvim-cmp gets rich body completion),
  -- else the protocol defaults.
  local capabilities = config and config.lua_ls and config.lua_ls.capabilities
  if not capabilities then
    local ok, cmp_lsp = pcall(require, "cmp_nvim_lsp")
    capabilities = ok and cmp_lsp.default_capabilities() or vim.lsp.protocol.make_client_capabilities()
  end

  vim.b[bufnr].evaluate_lua_attached = true
  vim.lsp.start({
    name = "evaluate_lua",
    cmd = cmd,
    root_dir = root,
    capabilities = capabilities,
    settings = lua_settings(libdirs),
    on_init = function(client)
      local rpc = client.rpc
      if not rpc or rpc.__evt_wrapped then return true end
      local orig = rpc.notify
      rpc.notify = function(method, params)
        if method == "textDocument/didOpen" and params and params.textDocument then
          params.textDocument.languageId = "lua" -- treat the .evt doc as Lua
          params.textDocument.text = blank(params.textDocument.text or "")
        elseif method == "textDocument/didChange" and params and params.textDocument then
          -- Resync the whole blanked buffer (a rangeless change = full replace, valid under
          -- any negotiated sync mode). Frontmatter and body positions stay identical.
          local b = vim.uri_to_bufnr(params.textDocument.uri)
          if vim.api.nvim_buf_is_loaded(b) then
            local full = table.concat(vim.api.nvim_buf_get_lines(b, 0, -1, false), "\n")
            params.contentChanges = { { text = blank(full) } }
          end
        end
        return orig(method, params)
      end
      rpc.__evt_wrapped = true
      return true
    end,
  }, { bufnr = bufnr })
end

return M
