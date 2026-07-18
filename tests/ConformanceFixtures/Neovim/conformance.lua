local uv = vim.uv
local apphost, solution, directory, global_json = arg[1], arg[2], arg[3], arg[4]

local stdin = uv.new_pipe(false)
local stdout = uv.new_pipe(false)
local stderr = uv.new_pipe(false)
local pending, frames, notifications, errors = "", {}, {}, ""
local exited, exit_code, exit_signal = false, nil, nil

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
  stdin:write(vim.mpack.encode({ 0, id, method, parameters }))
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
response(1)

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
notification("workspace/delta")

local project = directory .. "/src/Included.csproj"
local contents = vim.fn.readfile(project)

for index, line in ipairs(contents) do
  contents[index] = line:gsub("initial", "refreshed")
end

vim.fn.writefile(contents, project)
send(4, "workspace/refresh", vim.empty_dict())
response(4)
notification("workspace/delta")

if not uv.fs_copyfile(global_json, directory .. "/global.json") then
  fail("could not copy global.json for the reset lifecycle")
end

notification("workspace/reset")
send(5, "workspace/export", vim.empty_dict())
local export = response(5)
send(6, "operation/cancel", { operationId = export.operationId })
response(6)

local completed = notification("operation/completed")

if completed.outcome == nil then
  fail("the export did not complete")
end

send(7, "shutdown", vim.empty_dict())
local shutdown = response(7)

if shutdown.accepted ~= true then
  fail("the apphost did not accept shutdown")
end

stdin:close()

while not exited do
  uv.run("once")
end

if exit_code ~= 0 or exit_signal ~= 0 then
  fail("apphost shutdown failed")
end

print("Neovim conformance passed")
