local uv = vim.uv
local apphost, solution, directory, global_json = arg[1], arg[2], arg[3], arg[4]

local stdin = uv.new_pipe(false)
local stdout = uv.new_pipe(false)
local stderr = uv.new_pipe(false)
local pending, frames, notifications, errors = "", {}, {}, ""
local exited, exit_code, exit_signal = false, nil, nil
local operation_id, completion_count = nil, 0

local function fail(message)
  error(message .. (errors == "" and "" or ": " .. errors))
end

local function decode()
  while #pending > 0 do
    local frame, next_offset = vim.mpack.Unpacker()(pending, 1)

    if frame == nil then
      return
    end

    pending = pending:sub(next_offset)

    table.insert(frames, frame)
  end
end

local child, spawn_error = uv.spawn(apphost, {
  args = { "solution", solution, "--pipe" },
  stdio = { stdin, stdout, stderr },
}, function(code, signal)
  exited, exit_code, exit_signal = true, code, signal
end)

if child == nil then
  fail("could not start apphost: " .. tostring(spawn_error))
end

uv.read_start(stdout, function(error, data)
  if error ~= nil then
    fail("apphost stdout failed: " .. error)
  end

  if data ~= nil then
    pending = pending .. data
    decode()
  end
end)

uv.read_start(stderr, function(_, data)
  if data ~= nil then
    errors = errors .. data
  end
end)

local function next_frame()
  while #frames == 0 do
    if exited then
      fail("apphost exited before its next frame")
    end

    uv.run("once")
  end

  return table.remove(frames, 1)
end

local function record_notification(frame)
  if frame[1] == 2 then
    if frame[2] == "operation/completed" and operation_id ~= nil and frame[3].operationId == operation_id then
      completion_count = completion_count + 1
    end

    table.insert(notifications, frame)
    return true
  end

  return false
end

local function response(id)
  while true do
    local frame = next_frame()

    if not record_notification(frame) then
      if frame[1] ~= 1 or frame[2] ~= id then
        fail("unexpected response frame")
      end

      if frame[3] ~= vim.NIL then
        fail("request " .. id .. " failed: " .. tostring(frame[3].code))
      end

      return frame[4]
    end
  end
end

local function notification(method)
  while true do
    for index, frame in ipairs(notifications) do
      if frame[2] == method then
        table.remove(notifications, index)
        return frame[3]
      end
    end

    local frame = next_frame()

    if not record_notification(frame) then
      fail("expected " .. method .. " notification")
    end
  end
end

local function send(id, method, parameters)
  local written, write_error = false, nil
  stdin:write(vim.mpack.encode({ 0, id, method, parameters }), function(error)
    write_error = error
    written = true
  end)
  while not written do
    uv.run("once")
  end

  if write_error ~= nil then
    fail("apphost stdin failed: " .. write_error)
  end
end

send(1, "initialize", {
  protocolVersion = { major = 1, minor = 0 },
  clientInfo = { name = "conformance-neovim" },
  capabilities = {
    "workspace.root",
    "workspace.children",
    "workspace.refresh",
    "workspace.delta",
    "workspace.reset",
    "workspace.export",
    "operation.cancel",
  },
  limits = { maxFrameBytes = 65536, maxPageSize = 16 },
})
local initialized = response(1)
local workspace_id = initialized.workspace.id

send(2, "workspace/root", vim.empty_dict())
local root = response(2)
local project_id = nil

for _, node in ipairs(root.nodes) do
  if node.kind == "project" and node.name == "Included" then
    project_id = node.id
    break
  end
end

if project_id == nil then
  fail("the canonical project was not in the root page")
end

send(3, "workspace/children", { parentId = project_id, pageSize = 1 })
local page = response(3)
local page_delta = notification("workspace/delta")

if page_delta.workspaceId ~= workspace_id or page_delta.baseRevision ~= root.revision or page_delta.newRevision ~= page.revision then
  fail("the page delta was not part of the expected workspace lifecycle")
end

local project = directory .. "/Included.csproj"
local contents = vim.fn.readfile(project)
local replacements = 0

for index, line in ipairs(contents) do
  local updated, count = line:gsub("initial", "refreshed")
  contents[index] = updated
  replacements = replacements + count
end

if replacements < 1 then
  fail("the conformance marker was not refreshed")
end

vim.fn.writefile(contents, project)
send(4, "workspace/refresh", vim.empty_dict())
local refreshed = response(4)
local refresh_delta = notification("workspace/delta")

if refresh_delta.workspaceId ~= workspace_id or refresh_delta.newRevision ~= refreshed.revision or refresh_delta.newRevision <= page.revision then
  fail("the refresh delta was not part of the expected workspace lifecycle")
end

if not uv.fs_copyfile(global_json, directory .. "/global.json") then
  fail("could not copy global.json for the reset lifecycle")
end

local reset = notification("workspace/reset")

if reset.workspaceId ~= workspace_id or reset.revision <= refreshed.revision then
  fail("the reset was not part of the expected workspace lifecycle")
end

send(5, "workspace/root", vim.empty_dict())
local rebased = response(5)

if rebased.revision ~= reset.revision then
  fail("the reset did not require a matching root rebase")
end

send(6, "workspace/export", vim.empty_dict())
local export = response(6)
operation_id = export.operationId
send(7, "operation/cancel", { operationId = operation_id })
local cancellation = response(7)

if cancellation.accepted ~= true then
  fail("the apphost did not accept the export cancellation")
end

local completed = notification("operation/completed")

if completed.operationId ~= operation_id or completed.outcome ~= "cancelled" or completion_count ~= 1 then
  fail("the export cancellation did not complete exactly once")
end

send(8, "shutdown", vim.empty_dict())
local shutdown = response(8)

if shutdown.accepted ~= true then
  fail("the apphost did not accept shutdown")
end

stdin:close()

while not exited do
  uv.run("once")
end

while #frames > 0 do
  record_notification(table.remove(frames, 1))
end

if completion_count ~= 1 or exit_code ~= 0 or exit_signal ~= 0 then
  fail("apphost shutdown failed")
end

print("Neovim conformance passed")
