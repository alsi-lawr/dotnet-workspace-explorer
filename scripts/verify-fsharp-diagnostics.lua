local overall_timeout_ms = 300000
local quiet_timeout_ms = 15000

local function fail(message)
  io.stderr:write("F# diagnostics: " .. message .. "\n")
  vim.cmd("cquit 1")
end

local root = vim.fn.getcwd()
local files = vim.fn.systemlist({ "git", "ls-files", "*.fs", "*.fsi", "*.fsx" })

if vim.v.shell_error ~= 0 or #files == 0 then
  fail("could not enumerate tracked F# files")
  return
end

local expected = {}
for _, path in ipairs(files) do
  expected[vim.uri_from_fname(root .. "/" .. path)] = path
end

local attached = {}
local published = {}
local analyzed = {}
local diagnostics = {}
local last_event = vim.uv.now()

local function record(uri, kind)
  if expected[uri] then
    kind[uri] = true
    last_event = vim.uv.now()
  end
end

local function document_uri(params)
  if type(params) ~= "table" then
    return nil
  end

  if type(params.textDocument) == "table" then
    return params.textDocument.uri
  end

  return params.uri
end

local client_id = vim.lsp.start({
  name = "dotnet-cli-plus-fsautocomplete",
  cmd = {
    "dotnet",
    "fsautocomplete",
    "--adaptive-lsp-server-enabled",
    "--state-directory",
    root .. "/.agent-workspace/fsautocomplete",
  },
  root_dir = root,
  init_options = { AutomaticWorkspaceInit = true },
  settings = {
    FSharp = {
      UnusedOpensAnalyzer = true,
      UnusedDeclarationsAnalyzer = true,
      SimplifyNameAnalyzer = true,
      Linter = true,
      UseSdkScripts = true,
    },
  },
  handlers = {
    ["textDocument/publishDiagnostics"] = function(_, result)
      if result and expected[result.uri] then
        record(result.uri, published)
        diagnostics[result.uri] = result.diagnostics or {}
      end
    end,
    ["fsharp/documentAnalyzed"] = function(_, result)
      local uri = document_uri(result)
      if uri then
        record(uri, analyzed)
      end
    end,
  },
})

if not client_id then
  fail("could not start the pinned FsAutoComplete client")
  return
end

for uri, _ in pairs(expected) do
  local buffer = vim.fn.bufadd(vim.uri_to_fname(uri))
  vim.fn.bufload(buffer)
  vim.bo[buffer].filetype = "fsharp"
  vim.lsp.buf_attach_client(buffer, client_id)
  attached[uri] = true
end

local function missing(events)
  local paths = {}
  for uri, path in pairs(expected) do
    if not events[uri] then
      table.insert(paths, path)
    end
  end
  table.sort(paths)
  return paths
end

local function ready()
  return #missing(attached) == 0 and #missing(published) == 0 and #missing(analyzed) == 0
end

if not vim.wait(overall_timeout_ms, ready, 100) then
  fail("timed out waiting for final publications; missing attachments="
    .. table.concat(missing(attached), ", ")
    .. "; publications=" .. table.concat(missing(published), ", ")
    .. "; analysis=" .. table.concat(missing(analyzed), ", "))
  return
end

if not vim.wait(quiet_timeout_ms, function()
  return vim.uv.now() - last_event >= quiet_timeout_ms
end, 100) then
  fail("timed out waiting for the final diagnostic quiet window")
  return
end

local failures = {}
for uri, path in pairs(expected) do
  for _, diagnostic in ipairs(diagnostics[uri] or {}) do
    table.insert(failures, string.format(
      "%s:%d:%d [%s] %s",
      path,
      diagnostic.range.start.line + 1,
      diagnostic.range.start.character + 1,
      diagnostic.code or "diagnostic",
      diagnostic.message
    ))
  end
end

table.sort(failures)
if #failures > 0 then
  fail("FsAutoComplete reported diagnostics:\n" .. table.concat(failures, "\n"))
  return
end

vim.lsp.stop_client(client_id, true)
io.stdout:write(string.format(
  "F# diagnostics: %d tracked files attached, published, analyzed, and quiet for %d ms with zero diagnostics",
  #files,
  quiet_timeout_ms
) .. "\n")
vim.cmd("qa!")
