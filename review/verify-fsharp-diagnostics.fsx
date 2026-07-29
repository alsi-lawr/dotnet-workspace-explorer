open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

let fail message =
    raise (InvalidOperationException message)

let require condition message =
    if not condition then
        fail message

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let quietWindow = TimeSpan.FromSeconds 15.0

let target =
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [| path |] ->
        if Path.IsPathRooted path then
            Path.GetFullPath path
        else
            Path.GetFullPath(Path.Combine(root, path))
    | _ ->
        fail
            "Usage: dotnet fsi review/verify-fsharp-diagnostics.fsx -- <repository-file.fs|fsi|fsx>"

let relativeTarget = Path.GetRelativePath(root, target)

require
    (not (Path.IsPathRooted relativeTarget)
     && relativeTarget <> ".."
     && not (
         relativeTarget.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
     ))
    "The review target must be inside the repository."

require (File.Exists target) $"The review target does not exist: {relativeTarget}"

require
    ([ ".fs"; ".fsi"; ".fsx" ]
     |> List.contains (Path.GetExtension(target).ToLowerInvariant()))
    "The review target must be an F# source or script file."

let rec findProject directory =
    match Directory.GetFiles(directory, "*.fsproj") with
    | [| project |] -> Some project
    | [||] when directory = root -> None
    | [||] ->
        Directory.GetParent directory
        |> Option.ofObj
        |> Option.bind (fun parent -> findProject parent.FullName)
    | _ -> fail $"The review target has multiple candidate F# projects in {directory}."

let project =
    if Path.GetExtension(target).Equals(".fsx", StringComparison.OrdinalIgnoreCase) then
        None
    else
        Path.GetDirectoryName target |> Option.ofObj |> Option.bind findProject

let writeBytes (stream: Stream) (body: byte array) =
    let header = Encoding.ASCII.GetBytes $"Content-Length: {body.Length}\r\n\r\n"
    stream.Write(header, 0, header.Length)
    stream.Write(body, 0, body.Length)
    stream.Flush()

let send (stream: Stream) value =
    JsonSerializer.SerializeToUtf8Bytes value |> writeBytes stream

let request stream id methodName parameters =
    send
        stream
        {| jsonrpc = "2.0"
           id = id
           method = methodName
           ``params`` = parameters |}

let notify stream methodName parameters =
    send
        stream
        {| jsonrpc = "2.0"
           method = methodName
           ``params`` = parameters |}

let readAsync (stream: Stream) cancellationToken =
    task {
        let header = ResizeArray<byte>()
        let single = Array.zeroCreate<byte> 1
        let mutable complete = false

        while not complete do
            let! count = stream.ReadAsync(single.AsMemory(), cancellationToken)
            require (count = 1) "FsAutoComplete stdout ended inside an LSP header."
            header.Add single[0]

            complete <-
                header.Count >= 4
                && header[header.Count - 4] = byte '\r'
                && header[header.Count - 3] = byte '\n'
                && header[header.Count - 2] = byte '\r'
                && header[header.Count - 1] = byte '\n'

        let headerText = Encoding.ASCII.GetString(header.ToArray())

        let length =
            headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryPick (fun line ->
                let separator = line.IndexOf ':'

                if
                    separator > 0
                    && line[.. separator - 1]
                        .Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                then
                    match Int32.TryParse(line[separator + 1 ..].Trim()) with
                    | true, value -> Some value
                    | _ -> None
                else
                    None)
            |> Option.defaultWith (fun () -> fail "LSP message has no Content-Length.")

        require (length >= 0 && length <= 16 * 1024 * 1024) "Invalid LSP Content-Length."
        let body = Array.zeroCreate<byte> length
        let mutable offset = 0

        while offset < length do
            let! count = stream.ReadAsync(body.AsMemory offset, cancellationToken)
            require (count > 0) "FsAutoComplete stdout ended inside an LSP body."
            offset <- offset + count

        return JsonDocument.Parse body
    }

let property name (value: JsonElement) =
    match value.TryGetProperty(name: string) with
    | true, found -> Some found
    | _ -> None

let fsharpSettings =
    {| UnusedOpensAnalyzer = true
       UnusedDeclarationsAnalyzer = true
       SimplifyNameAnalyzer = true
       Linter = true
       UseSdkScripts = true |}

let rootUri = Uri(root + string Path.DirectorySeparatorChar).AbsoluteUri
let targetUri = Uri(target).AbsoluteUri

let state =
    Path.Combine(root, ".agent-workspace", "review", $"fsac-{Guid.NewGuid():N}")

Directory.CreateDirectory state |> ignore

let start = ProcessStartInfo "dotnet"
start.WorkingDirectory <- __SOURCE_DIRECTORY__
start.UseShellExecute <- false
start.RedirectStandardInput <- true
start.RedirectStandardOutput <- true
start.RedirectStandardError <- true
start.CreateNoWindow <- true

for argument in
    [ "tool"
      "run"
      "fsautocomplete"
      "--"
      "--adaptive-lsp-server-enabled"
      "--state-directory"
      state ] do
    start.ArgumentList.Add argument

let server = new Process(StartInfo = start, EnableRaisingEvents = true)
let cancellation = new CancellationTokenSource()
let mutable plannedExit = false

let cancelHandler =
    ConsoleCancelEventHandler(fun _ event ->
        event.Cancel <- true
        cancellation.Cancel())

let exitHandler = EventHandler(fun _ _ -> cancellation.Cancel())
Console.CancelKeyPress.AddHandler cancelHandler
AppDomain.CurrentDomain.ProcessExit.AddHandler exitHandler
require (server.Start()) "Could not start the pinned FsAutoComplete server."

server.Exited.Add(fun _ ->
    if not plannedExit then
        cancellation.Cancel())

let serverError = server.StandardError.ReadToEndAsync()

let read () =
    readAsync server.StandardOutput.BaseStream cancellation.Token
    |> _.GetAwaiter().GetResult()

let respondToServer (message: JsonElement) =
    match property "id" message, property "method" message with
    | Some id, Some methodName when methodName.ValueKind = JsonValueKind.String ->
        let result =
            match methodName.GetString() with
            | "workspace/configuration" ->
                let count =
                    property "params" message
                    |> Option.bind (property "items")
                    |> Option.filter (fun items -> items.ValueKind = JsonValueKind.Array)
                    |> Option.map _.GetArrayLength()
                    |> Option.defaultValue 0

                Array.init count (fun _ -> fsharpSettings) |> JsonSerializer.SerializeToNode
            | "workspace/workspaceFolders" ->
                [| {| uri = rootUri
                      name = DirectoryInfo(root).Name |} |]
                |> JsonSerializer.SerializeToNode
            | _ -> null

        let response = JsonObject()
        response["jsonrpc"] <- JsonValue.Create "2.0"
        response["id"] <- JsonNode.Parse(id.GetRawText())
        response["result"] <- result
        send server.StandardInput.BaseStream response
        true
    | _ -> false

let waitForResponse id =
    let mutable completed = false

    while not completed do
        use message = read ()

        if not (respondToServer message.RootElement) then
            match property "id" message.RootElement with
            | Some actual when actual.ValueKind = JsonValueKind.Number && actual.GetInt32() = id ->
                match property "error" message.RootElement with
                | Some error when error.ValueKind <> JsonValueKind.Null ->
                    fail $"FsAutoComplete request {id} failed: {error.GetRawText()}"
                | _ -> completed <- true
            | _ -> ()

let targetMessage parameters =
    let uri =
        property "textDocument" parameters
        |> Option.bind (property "uri")
        |> Option.orElseWith (fun () -> property "uri" parameters)

    uri
    |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
    |> Option.map _.GetString()
    |> Option.bind Option.ofObj
    |> Option.exists (fun value ->
        let path = Uri(value).LocalPath |> Path.GetFullPath

        path.Equals(
            target,
            if OperatingSystem.IsWindows() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal
        ))

let mutable stopped = false

try
    request
        server.StandardInput.BaseStream
        1
        "initialize"
        {| processId = Environment.ProcessId
           rootPath = root
           rootUri = rootUri
           capabilities =
            {| workspace =
                {| configuration = true
                   workspaceFolders = true |}
               textDocument = {| publishDiagnostics = {| relatedInformation = true |} |} |}
           initializationOptions = {| AutomaticWorkspaceInit = false |}
           workspaceFolders =
            [| {| uri = rootUri
                  name = DirectoryInfo(root).Name |} |] |}

    waitForResponse 1
    notify server.StandardInput.BaseStream "initialized" {| |}

    notify
        server.StandardInput.BaseStream
        "workspace/didChangeConfiguration"
        {| settings = {| FSharp = fsharpSettings |} |}

    project
    |> Option.iter (fun path ->
        request
            server.StandardInput.BaseStream
            2
            "fsharp/workspaceLoad"
            {| textDocuments = [| {| uri = Uri(path).AbsoluteUri |} |] |}

        waitForResponse 2)

    notify
        server.StandardInput.BaseStream
        "textDocument/didOpen"
        {| textDocument =
            {| uri = targetUri
               languageId = "fsharp"
               version = 1
               text = File.ReadAllText target |} |}

    let mutable diagnostics = None
    let mutable analyzed = false
    let mutable lastTargetEvent = Stopwatch.GetTimestamp()
    let mutable settled = false

    let elapsed () =
        TimeSpan.FromSeconds(
            float (Stopwatch.GetTimestamp() - lastTargetEvent) / float Stopwatch.Frequency
        )

    while not settled do
        let ready = diagnostics.IsSome && analyzed

        let received =
            if not ready then
                Some(read ())
            else
                let remaining = quietWindow - elapsed ()

                if remaining <= TimeSpan.Zero then
                    None
                else
                    use quietCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource cancellation.Token

                    let reading = readAsync server.StandardOutput.BaseStream quietCancellation.Token

                    let quiet = Task.Delay(remaining, cancellation.Token)
                    let winner = Task.WhenAny([| reading :> Task; quiet |]).GetAwaiter().GetResult()

                    if Object.ReferenceEquals(winner, quiet) then
                        cancellation.Token.ThrowIfCancellationRequested()
                        quietCancellation.Cancel()

                        try
                            reading.GetAwaiter().GetResult().Dispose()
                        with :? OperationCanceledException ->
                            ()

                        None
                    else
                        Some(reading.GetAwaiter().GetResult())

        match received with
        | None -> settled <- true
        | Some message ->
            use message = message

            if not (respondToServer message.RootElement) then
                match
                    property "method" message.RootElement, property "params" message.RootElement
                with
                | Some methodName, Some parameters when
                    methodName.GetString() = "textDocument/publishDiagnostics"
                    && targetMessage parameters
                    ->
                    diagnostics <-
                        property "diagnostics" parameters
                        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.Array)
                        |> Option.map (fun value ->
                            value.EnumerateArray() |> Seq.map _.Clone() |> Seq.toArray)
                        |> Option.orElse (Some [||])

                    lastTargetEvent <- Stopwatch.GetTimestamp()
                | Some methodName, Some parameters when
                    methodName.GetString() = "fsharp/documentAnalyzed" && targetMessage parameters
                    ->
                    analyzed <- true
                    lastTargetEvent <- Stopwatch.GetTimestamp()
                | _ -> ()

    let failures =
        diagnostics.Value
        |> Array.map (fun diagnostic ->
            let position = diagnostic.GetProperty("range").GetProperty "start"
            let line = position.GetProperty("line").GetInt32() + 1
            let character = position.GetProperty("character").GetInt32() + 1

            let code =
                property "code" diagnostic
                |> Option.map (fun value ->
                    if value.ValueKind = JsonValueKind.String then
                        value.GetString()
                    else
                        value.GetRawText())
                |> Option.bind Option.ofObj
                |> Option.defaultValue "diagnostic"

            let message = diagnostic.GetProperty("message").GetString()
            $"{relativeTarget}:{line}:{character} [{code}] {message}")
        |> Array.sort

    require
        (failures.Length = 0)
        $"FsAutoComplete reported diagnostics:\n{String.Join(Environment.NewLine, failures)}"

    request server.StandardInput.BaseStream 3 "shutdown" {| |}
    waitForResponse 3
    plannedExit <- true
    notify server.StandardInput.BaseStream "exit" {| |}
    server.StandardInput.Close()
    server.WaitForExit()
    stopped <- true
    require (server.ExitCode = 0) $"FsAutoComplete exited {server.ExitCode}:\n{serverError.Result}"

    printfn
        "F# diagnostics: %s published and analyzed, quiet for %d ms, zero diagnostics"
        relativeTarget
        (int quietWindow.TotalMilliseconds)
finally
    Console.CancelKeyPress.RemoveHandler cancelHandler
    AppDomain.CurrentDomain.ProcessExit.RemoveHandler exitHandler

    if not stopped && not server.HasExited then
        server.Kill true
        server.WaitForExit()

    if Directory.Exists state then
        Directory.Delete(state, true)

    cancellation.Dispose()
    server.Dispose()
