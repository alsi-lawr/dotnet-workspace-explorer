module Dotnet.CLI.Plus.SystemCapacity

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Dotnet.CLI.Plus.Transport

type Configuration =
    { BuildConfiguration: string
      Projects: int
      ItemsPerProject: int
      WorkerCapacities: int array
      OutputPath: string }

type CapacityResult =
    { WorkerCapacity: int
      RootMilliseconds: float
      ExportMilliseconds: float
      TotalMilliseconds: float
      ExportedNodeCount: int
      ExportChunkCount: int
      PeakAggregateRssBytes: int64
      PeakProcessCount: int
      RssSamples: int }

type CapacityReport =
    { SchemaVersion: int
      CreatedAtUtc: DateTime
      Runtime: string
      OperatingSystem: string
      ProcessorCount: int
      Projects: int
      ItemsPerProject: int
      Results: CapacityResult array }

let fail message =
    raise (InvalidOperationException message)

let require condition message =
    if not condition then
        fail message

let repositoryRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let parsePositive name (value: string) =
    match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
    | true, number when number > 0 -> number
    | _ -> fail $"{name} must be a positive integer."

let parseArguments arguments =
    let rec parse configuration remaining =
        match remaining with
        | [] -> configuration
        | "--configuration" :: value :: tail when value = "Debug" || value = "Release" ->
            parse
                { configuration with
                    BuildConfiguration = value }
                tail
        | "--projects" :: value :: tail ->
            parse
                { configuration with
                    Projects = parsePositive "--projects" value }
                tail
        | "--items" :: value :: tail ->
            parse
                { configuration with
                    ItemsPerProject = parsePositive "--items" value }
                tail
        | "--workers" :: value :: tail ->
            let capacities =
                value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (parsePositive "--workers")
                |> Array.distinct

            require (capacities.Length > 0) "--workers requires at least one capacity."

            parse
                { configuration with
                    WorkerCapacities = capacities }
                tail
        | "--output" :: value :: tail when not (String.IsNullOrWhiteSpace value) ->
            parse
                { configuration with
                    OutputPath = Path.GetFullPath value }
                tail
        | _ ->
            fail (
                "Usage: dotnet run --project benchmarks/Dotnet.CLI.Plus.SystemCapacity -c Release -- "
                + "--configuration Release [--projects 12] [--items 40] "
                + "[--workers 1,3] [--output path]"
            )

    let defaultOutput =
        Path.Combine(
            repositoryRoot,
            ".agent-workspace",
            "benchmarks",
            $"system-capacity-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json"
        )

    parse
        { BuildConfiguration = "Release"
          Projects = 12
          ItemsPerProject = 40
          WorkerCapacities = [| 1; 3 |]
          OutputPath = defaultOutput }
        (arguments |> Array.toList)

let apphostPath configuration =
    let executable =
        if OperatingSystem.IsWindows() then
            "Dotnet.CLI.Plus.exe"
        else
            "Dotnet.CLI.Plus"

    Path.Combine(
        repositoryRoot,
        "src",
        "Dotnet.CLI.Plus",
        "bin",
        configuration,
        "net10.0",
        executable
    )

let writeCorpus root projects itemsPerProject =
    Directory.CreateDirectory root |> ignore
    File.Copy(Path.Combine(repositoryRoot, "global.json"), Path.Combine(root, "global.json"))
    let solution = Path.Combine(root, "Capacity.slnx")
    use solutionWriter = new StreamWriter(solution, false, UTF8Encoding false)
    solutionWriter.WriteLine "<Solution>"
    solutionWriter.WriteLine "    <Folder Name=\"/src/\">"

    for projectNumber in 1..projects do
        let name = $"P{projectNumber:D4}"
        let relativeProject = $"src/{name}/{name}.csproj"
        solutionWriter.WriteLine $"        <Project Path=\"{relativeProject}\" Type=\"C#\" />"
        let projectDirectory = Path.Combine(root, "src", name)
        let itemsDirectory = Path.Combine(projectDirectory, "Items")
        Directory.CreateDirectory itemsDirectory |> ignore
        let projectPath = Path.Combine(projectDirectory, $"{name}.csproj")
        use projectWriter = new StreamWriter(projectPath, false, UTF8Encoding false)
        projectWriter.WriteLine "<Project Sdk=\"Microsoft.NET.Sdk\">"
        projectWriter.WriteLine "  <PropertyGroup>"
        projectWriter.WriteLine "    <TargetFramework>net10.0</TargetFramework>"
        projectWriter.WriteLine "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
        projectWriter.WriteLine "  </PropertyGroup>"
        projectWriter.WriteLine "  <ItemGroup>"

        for itemNumber in 1..itemsPerProject do
            let item = $"N{itemNumber:D4}.cs"
            projectWriter.WriteLine $"    <Compile Include=\"Items/{item}\" />"

            File.WriteAllText(
                Path.Combine(itemsDirectory, item),
                $"namespace {name}; class N{itemNumber:D4} {{}}"
            )

        projectWriter.WriteLine "  </ItemGroup>"
        projectWriter.WriteLine "</Project>"

    solutionWriter.WriteLine "    </Folder>"
    solutionWriter.WriteLine "</Solution>"
    solution

let request id methodName parameters =
    RpcCodec.encodeFrame (Request(id, methodName, parameters))

let send (child: Process) frame =
    child.StandardInput.BaseStream.Write(frame, 0, frame.Length)
    child.StandardInput.BaseStream.Flush()

let readFrame (child: Process) =
    let pending = ResizeArray<byte>()
    let mutable result = None

    while result.IsNone do
        let value = child.StandardOutput.BaseStream.ReadByte()
        require (value >= 0) "The apphost stdout ended before a complete frame arrived."
        pending.Add(byte value)

        match RpcCodec.tryReadValueLength RpcCodec.secureLimits (pending.ToArray()) with
        | Error RpcDecodeError.Incomplete -> ()
        | Error error -> fail $"The apphost emitted invalid MessagePack: {error}"
        | Ok length when length = pending.Count ->
            match RpcCodec.decodeFrame RpcCodec.secureLimits (pending.ToArray()) with
            | Ok(RpcFrameDecodeResult.Frame frame) -> result <- Some frame
            | Ok(RpcFrameDecodeResult.RecoverableError _) ->
                fail "The apphost stdout contained a recoverable request error."
            | Error error -> fail $"The apphost emitted an invalid frame: {error}"
        | Ok _ -> fail "The frame reader consumed an unexpected byte count."

    result.Value

let response expectedId =
    function
    | Response(id, None, result) when id = expectedId -> result
    | Response(id, Some error, _) when id = expectedId ->
        fail $"Request {id} failed: {error.Code}: {error.Message}"
    | frame -> fail $"Expected response {expectedId}, got {frame}."

let field name value =
    value |> RpcValue.requireMap "parameters" |> RpcValue.requireField name

let initialize =
    RpcValue.map
        [ "protocolVersion",
          RpcValue.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
          "clientInfo", RpcValue.map [ "name", RpcValue.String "system-capacity-benchmark" ]
          "capabilities",
          RpcValue.array [ RpcValue.String "workspace.root"; RpcValue.String "workspace.export" ]
          "limits",
          RpcValue.map
              [ "maxFrameBytes", RpcValue.Integer 65536L
                "maxPageSize", RpcValue.Integer 100L ] ]

let procSnapshot () =
    Directory.EnumerateDirectories "/proc"
    |> Seq.choose (fun directory ->
        let name = Path.GetFileName directory

        match Int32.TryParse name with
        | false, _ -> None
        | true, pid ->
            try
                let stat = File.ReadAllText(Path.Combine(directory, "stat"))
                let afterName = stat.LastIndexOf ')'

                if afterName < 0 then
                    None
                else
                    let fields =
                        stat[afterName + 2 ..].Split(' ', StringSplitOptions.RemoveEmptyEntries)

                    let parent = Int32.Parse(fields[1], CultureInfo.InvariantCulture)

                    let rss =
                        File.ReadLines(Path.Combine(directory, "status"))
                        |> Seq.tryFind (fun line ->
                            line.StartsWith("VmRSS:", StringComparison.Ordinal))
                        |> Option.map (fun line ->
                            line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]
                            |> Int64.Parse
                            |> fun kibibytes -> kibibytes * 1024L)
                        |> Option.defaultValue 0L

                    Some(pid, parent, rss)
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? InvalidOperationException
            | :? FormatException
            | :? IndexOutOfRangeException -> None)
    |> Seq.toArray

type RssSampler(rootPid: int) =
    let cancellation = new CancellationTokenSource()
    let mutable peakBytes = 0L
    let mutable peakProcesses = 0
    let mutable samples = 0

    let thread =
        Thread(
            ThreadStart(fun () ->
                while not cancellation.IsCancellationRequested do
                    let snapshot = procSnapshot ()
                    let tree = HashSet<int>()
                    tree.Add rootPid |> ignore
                    let mutable changed = true

                    while changed do
                        changed <- false

                        for pid, parent, _ in snapshot do
                            if tree.Contains parent && tree.Add pid then
                                changed <- true

                    let aggregate =
                        snapshot
                        |> Array.sumBy (fun (pid, _, rss) -> if tree.Contains pid then rss else 0L)

                    peakBytes <- max peakBytes aggregate
                    peakProcesses <- max peakProcesses tree.Count
                    samples <- samples + 1
                    Thread.Sleep 10)
        )

    do
        thread.IsBackground <- true
        thread.Start()

    member _.Stop() =
        cancellation.Cancel()
        thread.Join()
        cancellation.Dispose()
        peakBytes, peakProcesses, samples

let millisecondsSince timestamp =
    float (Stopwatch.GetTimestamp() - timestamp) * 1000.0
    / float Stopwatch.Frequency

let measure (configuration: Configuration) workerCapacity =
    let apphost = apphostPath configuration.BuildConfiguration

    require
        (File.Exists apphost)
        $"Build the {configuration.BuildConfiguration} apphost first: {apphost}"

    let corpus =
        Path.Combine(
            repositoryRoot,
            ".agent-workspace",
            "benchmarks",
            $"corpus-{workerCapacity}-{Guid.NewGuid():N}"
        )

    let solution =
        writeCorpus corpus configuration.Projects configuration.ItemsPerProject

    let start = ProcessStartInfo apphost
    start.WorkingDirectory <- corpus
    start.UseShellExecute <- false
    start.RedirectStandardInput <- true
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.CreateNoWindow <- true

    for argument in [ "solution"; solution; "--pipe"; "--export-workers"; string workerCapacity ] do
        start.ArgumentList.Add argument

    use child = new Process(StartInfo = start)
    let mutable sampler = None

    try
        let totalStarted = Stopwatch.GetTimestamp()
        require (child.Start()) "Could not start the built apphost."
        let stderr = child.StandardError.ReadToEndAsync()
        sampler <- Some(new RssSampler(child.Id))
        send child (request 1u "initialize" initialize)
        readFrame child |> response 1u |> ignore
        send child (request 2u "workspace/root" RpcValue.emptyMap)
        let root = readFrame child |> response 2u
        field "revision" root |> RpcValue.requireInteger "revision" |> ignore
        let rootMilliseconds = millisecondsSince totalStarted
        let exportStarted = Stopwatch.GetTimestamp()
        send child (request 3u "workspace/export" RpcValue.emptyMap)
        let export = readFrame child |> response 3u
        let operationId = field "operationId" export |> RpcValue.requireString "operationId"
        let mutable nodes = 0
        let mutable chunks = 0
        let mutable completed = false

        while not completed do
            match readFrame child with
            | Notification("workspace/exportChunk", parameters) ->
                require
                    (field "operationId" parameters = RpcValue.String operationId)
                    "The export stream changed operation identity."

                nodes <-
                    nodes + (field "nodes" parameters |> RpcValue.requireArray "nodes" |> _.Length)

                chunks <- chunks + 1
            | Notification("operation/completed", parameters) ->
                require
                    (field "operationId" parameters = RpcValue.String operationId)
                    "The completion changed operation identity."

                require
                    (field "outcome" parameters = RpcValue.String "succeeded")
                    "The measured export did not succeed."

                completed <- true
            | frame -> fail $"Unexpected export frame: {frame}"

        let exportMilliseconds = millisecondsSince exportStarted
        send child (request 4u "shutdown" RpcValue.emptyMap)
        let shutdown = readFrame child |> response 4u
        require (field "accepted" shutdown = RpcValue.Boolean true) "Shutdown was not accepted."
        child.StandardInput.Close()
        require (child.WaitForExit 30000) "The measured apphost did not exit after shutdown."

        require
            (child.ExitCode = 0)
            $"The measured apphost exited {child.ExitCode}: {stderr.Result}"

        let totalMilliseconds = millisecondsSince totalStarted
        let peakRss, peakProcesses, sampleCount = sampler.Value.Stop()
        sampler <- None

        { WorkerCapacity = workerCapacity
          RootMilliseconds = rootMilliseconds
          ExportMilliseconds = exportMilliseconds
          TotalMilliseconds = totalMilliseconds
          ExportedNodeCount = nodes
          ExportChunkCount = chunks
          PeakAggregateRssBytes = peakRss
          PeakProcessCount = peakProcesses
          RssSamples = sampleCount }
    finally
        sampler |> Option.iter (fun value -> value.Stop() |> ignore)

        if not child.HasExited then
            child.Kill true
            child.WaitForExit()

        if Directory.Exists corpus then
            Directory.Delete(corpus, true)

module Program =
    [<EntryPoint>]
    let main arguments =
        require
            (OperatingSystem.IsLinux() && Directory.Exists "/proc")
            "Aggregate apphost-plus-worker RSS measurement requires Linux /proc."

        let configuration = parseArguments arguments
        let results = configuration.WorkerCapacities |> Array.map (measure configuration)

        let report =
            { SchemaVersion = 1
              CreatedAtUtc = DateTime.UtcNow
              Runtime = Environment.Version.ToString()
              OperatingSystem = Environment.OSVersion.ToString()
              ProcessorCount = Environment.ProcessorCount
              Projects = configuration.Projects
              ItemsPerProject = configuration.ItemsPerProject
              Results = results }

        Path.GetDirectoryName configuration.OutputPath
        |> Option.ofObj
        |> Option.filter (String.IsNullOrEmpty >> not)
        |> Option.iter (Directory.CreateDirectory >> ignore)

        File.WriteAllText(
            configuration.OutputPath,
            JsonSerializer.Serialize(report, JsonSerializerOptions(WriteIndented = true))
        )

        printfn "System capacity results: %s" configuration.OutputPath

        for result in results do
            printfn
                "workers=%d root=%.3f ms export=%.3f ms total=%.3f ms peak-rss=%d bytes processes=%d"
                result.WorkerCapacity
                result.RootMilliseconds
                result.ExportMilliseconds
                result.TotalMilliseconds
                result.PeakAggregateRssBytes
                result.PeakProcessCount

        0
