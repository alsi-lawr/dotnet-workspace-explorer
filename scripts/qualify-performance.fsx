#r "../src/Dotnet.CLI.Plus.Core/bin/Release/net10.0/Dotnet.CLI.Plus.Core.dll"
#r "../src/Dotnet.CLI.Plus.Transport/bin/Release/net10.0/Dotnet.CLI.Plus.Transport.dll"

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Transport

let fail message =
    raise (InvalidOperationException(message))

let require condition message =
    if not condition then
        fail message

let scriptPath = __SOURCE_DIRECTORY__
let repositoryRoot = Path.GetFullPath(Path.Combine(scriptPath, ".."))

let requestedConfiguration = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList
let configuration = "Release"

let invocationUtc = DateTime.UtcNow

let invocationId =
    invocationUtc.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)

let apphost =
    Path.Combine(
        repositoryRoot,
        "src",
        "Dotnet.CLI.Plus",
        "bin",
        configuration,
        "net10.0",
        "Dotnet.CLI.Plus"
    )

let coreAssembly =
    Path.Combine(
        repositoryRoot,
        "src",
        "Dotnet.CLI.Plus.Core",
        "bin",
        configuration,
        "net10.0",
        "Dotnet.CLI.Plus.Core.dll"
    )

let transportAssembly =
    Path.Combine(
        repositoryRoot,
        "src",
        "Dotnet.CLI.Plus.Transport",
        "bin",
        configuration,
        "net10.0",
        "Dotnet.CLI.Plus.Transport.dll"
    )

let runCommand executable (arguments: string list) =
    let start = ProcessStartInfo(executable)
    start.WorkingDirectory <- repositoryRoot
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.CreateNoWindow <- true

    for argument in arguments do
        start.ArgumentList.Add(argument)

    use child = new Process(StartInfo = start)
    require (child.Start()) $"Could not start {executable}."
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    child.ExitCode, stdout.Result.Trim(), stderr.Result.Trim()

let git arguments =
    let code, stdout, stderr = runCommand "git" arguments
    let rendered = String.Join(" ", arguments)
    require (code = 0) $"git {rendered} failed: {stderr}"
    stdout

let packageVersion =
    if File.Exists(coreAssembly) then
        let version = AssemblyName.GetAssemblyName(coreAssembly).Version
        if isNull version then "unresolved" else version.ToString(3)
    else
        "unresolved"

let commit = git [ "rev-parse"; "--short"; "HEAD" ]

let summaryDirectory =
    Path.Combine(repositoryRoot, "artifacts", "performance", $"{packageVersion}-{commit}")

let summaryPath = Path.Combine(summaryDirectory, "summary.json")

let corpusRoot =
    Path.Combine(repositoryRoot, ".agent-workspace", "performance", invocationId)

let mutable status = "non-qualifying"
let mutable reason = "qualification did not run"
let mutable cleanup = "not-started"
let mutable environment = Dictionary<string, obj>()
let mutable warmup: obj = null
let measured = ResizeArray<obj>()
let mutable medianRoot: float option = None
let mutable medianChange: float option = None
let mutable medianExport: float option = None
let mutable totalNodes = 0
let mutable generatedExpected = 250000
let mutable generatedObserved = 0
let mutable generatedUnique = 0
let mutable publicKinds = Dictionary<string, int>(StringComparer.Ordinal)
let mutable maxAggregateRss = 0L
let processRuns = ResizeArray<obj>()
let mutable processSummary: obj = null
let mutable summaryWritten = false

let jsonOptions = JsonSerializerOptions(WriteIndented = true)

let writeSummary () =
    if summaryWritten then
        fail "The qualification summary may be written exactly once."

    if File.Exists(summaryPath) then
        fail $"Refusing to overwrite the existing qualification summary: {summaryPath}"

    Directory.CreateDirectory(summaryDirectory) |> ignore

    let thresholds =
        dict
            [ "medianRootMilliseconds", box 2000.0
              "medianChangeMilliseconds", box 500.0
              "medianExportMilliseconds", box 30000.0
              "maximumAggregateRssBytes", box (int64 (1.5 * 1024.0 * 1024.0 * 1024.0)) ]

    let summary =
        dict
            [ "schemaVersion", box 1
              "status", box status
              "reason", box reason
              "packageVersion", box packageVersion
              "commit", box commit
              "invocationUtc", box (invocationUtc.ToString("O", CultureInfo.InvariantCulture))
              "invocationId", box invocationId
              "configuration", box configuration
              "environment", box environment
              "publicSequence",
              box
                  [| "initialize"
                     "workspace/root"
                     "workspace/children"
                     "external-project-file-edit"
                     "workspace/delta"
                     "workspace/export"
                     "workspace/exportChunk"
                     "operation/completed"
                     "shutdown" |]
              "effectiveDebounceMilliseconds", box 0
              "rssSampling",
              box (
                  dict
                      [ "method", box "Linux /proc recursive PID/PPID VmRSS aggregation"
                        "cadenceMilliseconds", box 10 ]
              )
              "warmup", warmup
              "measuredRuns", box (measured.ToArray())
              "mediansMilliseconds",
              box (
                  dict
                      [ "root", box medianRoot
                        "materializedChange", box medianChange
                        "export", box medianExport ]
              )
              "cardinality",
              box (
                  dict
                      [ "totalPublicNodes", box totalNodes
                        "publicKindCounts", box publicKinds
                        "generatedExpected", box generatedExpected
                        "generatedObserved", box generatedObserved
                        "generatedUnique", box generatedUnique ]
              )
              "maximumAggregateRssBytes", box maxAggregateRss
              "processEvidence", processSummary
              "cleanup", box cleanup
              "budgets", box thresholds ]

    use stream =
        new FileStream(summaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)

    JsonSerializer.Serialize(stream, summary, jsonOptions)
    stream.Flush(true)
    summaryWritten <- true

let cleanupCorpus () =
    if Directory.Exists(corpusRoot) then
        Directory.Delete(corpusRoot, true)

let normalizeWhitespace (value: string) =
    value.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
    |> String.concat " "
    |> fun item -> item.Trim().ToLowerInvariant()

let readProcKey key =
    File.ReadLines("/proc/cpuinfo")
    |> Seq.tryPick (fun line ->
        let separator = line.IndexOf(':')

        if separator > 0 && line.Substring(0, separator).Trim() = key then
            Some(line.Substring(separator + 1).Trim())
        else
            None)

let readMemInfo key =
    File.ReadLines("/proc/meminfo")
    |> Seq.tryPick (fun line ->
        if line.StartsWith(key + ":", StringComparison.Ordinal) then
            line.Split([| ':'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryPick (fun value ->
                match Int64.TryParse value with
                | true, number -> Some number
                | _ -> None)
        else
            None)

let rec existingPath path =
    if Directory.Exists(path) || File.Exists(path) then
        path
    else
        existingPath (Path.GetDirectoryName path)

let mountSource path =
    let code, stdout, stderr =
        runCommand "findmnt" [ "-no"; "SOURCE"; "-T"; existingPath path ]

    require
        (code = 0 && not (String.IsNullOrWhiteSpace stdout))
        $"Could not resolve the mount for {path}: {stderr}"

    stdout

let localNvme path =
    let source = mountSource path
    source.StartsWith("/dev/nvme", StringComparison.Ordinal)

let hostGate () =
    let evidence = Dictionary<string, obj>()
    let failures = ResizeArray<string>()
    let add name value = evidence[name] <- box value
    add "operatingSystem" Environment.OSVersion.VersionString
    add "architecture" (Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString())
    add "logicalProcessors" Environment.ProcessorCount
    let model = readProcKey "model name" |> Option.defaultValue ""
    add "cpuModel" model
    let memoryKiB = readMemInfo "MemTotal" |> Option.defaultValue 0L
    add "memoryTotalKiB" memoryKiB
    add "memoryTotalGiB" (float memoryKiB / 1024.0 / 1024.0)
    let sdkCode, sdk, sdkError = runCommand "dotnet" [ "--version" ]
    add "resolvedDotnetSdk" sdk
    add "dotnetSdkError" sdkError
    let rootNvme = localNvme repositoryRoot
    let corpusNvme = localNvme (Path.GetDirectoryName corpusRoot)
    let resultNvme = localNvme (Path.GetDirectoryName summaryDirectory)
    add "repositoryOnLocalNvme" rootNvme
    add "corpusOnLocalNvme" corpusNvme
    add "resultOnLocalNvme" resultNvme
    add "apphost" apphost

    add
        "releaseOutputsPresent"
        (File.Exists(apphost)
         && File.Exists(coreAssembly)
         && File.Exists(transportAssembly))

    add
        "networkPolicy"
        "The qualifier invokes no restore, build, pack, download, or network command; the apphost inherits telemetry opt-out."

    if
        not (
            OperatingSystem.IsLinux()
            && Runtime.InteropServices.RuntimeInformation.OSArchitecture = Runtime.InteropServices.Architecture.X64
        )
    then
        failures.Add("host is not Linux x86_64")

    if normalizeWhitespace model <> "amd ryzen 7 5700x3d 8-core processor" then
        failures.Add("CPU is not the normalized AMD Ryzen 7 5700X3D reference model")

    if Environment.ProcessorCount <> 16 then
        failures.Add("host does not expose exactly 16 logical processors")

    if memoryKiB < 46L * 1024L * 1024L || memoryKiB >= 48L * 1024L * 1024L then
        failures.Add("usable memory is not 46 GiB-class")

    if not (rootNvme && corpusNvme && resultNvme) then
        failures.Add("repository, corpus, and result are not all on local NVMe")

    if sdkCode <> 0 || not (sdk.StartsWith("10.", StringComparison.Ordinal)) then
        failures.Add("resolved SDK is not compatible .NET 10")

    if
        not (
            File.Exists(apphost)
            && File.Exists(coreAssembly)
            && File.Exists(transportAssembly)
        )
    then
        failures.Add("Release apphost or compiled transport references are absent")

    environment <- evidence
    failures |> Seq.toList

let writeCorpus directory =
    let solution = Path.Combine(directory, "Scale.slnx")
    Directory.CreateDirectory(directory) |> ignore
    use solutionWriter = new StreamWriter(solution, false, new UTF8Encoding(false))
    solutionWriter.WriteLine("<Solution>")

    for project in 1..500 do
        solutionWriter.WriteLine($"  <Project Path=\"src/P{project:D4}/P{project:D4}.csproj\" />")

    solutionWriter.WriteLine("</Solution>")

    for project in 1..500 do
        let projectDirectory = Path.Combine(directory, "src", $"P{project:D4}")
        Directory.CreateDirectory(projectDirectory) |> ignore

        use writer =
            new StreamWriter(
                Path.Combine(projectDirectory, $"P{project:D4}.csproj"),
                false,
                new UTF8Encoding(false)
            )

        writer.WriteLine("<Project Sdk=\"Microsoft.NET.Sdk\">")

        writer.WriteLine(
            "  <PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>"
        )

        writer.WriteLine("  <ItemGroup>")

        for item in 1..500 do
            writer.WriteLine($"    <Compile Include=\"Items/N{item:D4}.cs\" />")

        writer.WriteLine("  </ItemGroup>")
        writer.WriteLine("</Project>")

    let projects =
        Directory.EnumerateFiles(
            Path.Combine(directory, "src"),
            "*.csproj",
            SearchOption.AllDirectories
        )
        |> Seq.toArray

    let includes =
        projects
        |> Seq.sumBy (fun project ->
            File.ReadLines(project)
            |> Seq.filter (fun line ->
                line.Contains("<Compile Include=", StringComparison.Ordinal))
            |> Seq.length)

    require
        (projects.Length = 500 && includes = 250000)
        "The generated corpus is not the exact 500-project/250,000-item shape."

    solution

let fields value = RpcValue.requireMap "parameters" value

let field name value =
    value |> fields |> RpcValue.requireField name

let stringField name value =
    field name value |> RpcValue.requireString name

let integerField name value =
    field name value |> RpcValue.requireInteger name

let arrayField name value =
    field name value |> RpcValue.requireArray name

let map values = RpcValue.map values

let request id name parameters =
    RpcCodec.encodeFrame (RpcFrame.Request(id, name, parameters))

let readFrame (stream: Stream) timeoutMilliseconds =
    let pending = ResizeArray<byte>()
    let buffer = Array.zeroCreate<byte> 1

    let deadline =
        Stopwatch.GetTimestamp()
        + int64 timeoutMilliseconds * Stopwatch.Frequency / 1000L

    let remaining () =
        max 1 (int ((deadline - Stopwatch.GetTimestamp()) * 1000L / Stopwatch.Frequency))

    let mutable decoded = None

    while decoded.IsNone do
        use cancellation = new CancellationTokenSource()
        let read = stream.ReadAsync(buffer.AsMemory(), cancellation.Token).AsTask()

        if not (read.Wait(remaining ())) then
            cancellation.Cancel()
            fail "Timed out while waiting for an RPC frame."

        if read.Result = 0 then
            fail "The apphost closed stdout before completing an RPC frame."

        pending.Add(buffer[0])

        match RpcCodec.tryReadValueLength RpcCodec.secureLimits (pending.ToArray()) with
        | Error RpcDecodeError.Incomplete -> ()
        | Error error -> fail $"Invalid apphost RPC frame length: {error}"
        | Ok length when length <> pending.Count ->
            fail "The apphost RPC frame reader consumed an unexpected byte count."
        | Ok _ ->
            match RpcCodec.decodeFrame RpcCodec.secureLimits (pending.ToArray()) with
            | Ok(RpcFrameDecodeResult.Frame frame) -> decoded <- Some frame
            | Ok(RpcFrameDecodeResult.RecoverableError _) ->
                fail "The apphost returned a recoverable RPC request error."
            | Error error -> fail $"Invalid apphost RPC frame: {error}"

    decoded.Value

let send (child: Process) bytes =
    child.StandardInput.BaseStream.Write(bytes, 0, bytes.Length)
    child.StandardInput.BaseStream.Flush()

let response id =
    function
    | RpcFrame.Response(actual, error, result) when actual = id -> error, result
    | frame -> fail $"Expected response {id}, got {frame}."

type ProcSample =
    { TimestampMilliseconds: int64
      AggregateRssBytes: int64
      Processes: (int * int * int64) array }

type Sampler =
    { Stop: unit -> ProcSample array * string option * int array }

let procStat pid =
    let content = File.ReadAllText($"/proc/{pid}/stat")
    let close = content.LastIndexOf(')')

    if close < 0 then
        fail $"Malformed /proc/{pid}/stat."

    let values =
        content.Substring(close + 2).Split(' ', StringSplitOptions.RemoveEmptyEntries)

    require (values.Length > 1) $"Malformed /proc/{pid}/stat fields."
    Int32.Parse(values[1], CultureInfo.InvariantCulture)

let procRss pid =
    File.ReadLines($"/proc/{pid}/status")
    |> Seq.tryPick (fun line ->
        if line.StartsWith("VmRSS:", StringComparison.Ordinal) then
            line.Split([| ':'; ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryPick (fun value ->
                match Int64.TryParse value with
                | true, number -> Some(number * 1024L)
                | _ -> None)
        else
            None)
    |> Option.defaultWith (fun () -> fail $"/proc/{pid}/status has no VmRSS.")

let startSampler rootPid =
    let cancellation = new CancellationTokenSource()
    let samples = ResizeArray<ProcSample>()
    let known = HashSet<int>()
    let mutable fault: string option = None
    let started = Stopwatch.StartNew()

    let sample () =
        try
            let stats =
                Directory.EnumerateDirectories("/proc")
                |> Seq.choose (fun path ->
                    match Int32.TryParse(Path.GetFileName path) with
                    | true, pid ->
                        try
                            Some(pid, procStat pid)
                        with _ ->
                            None
                    | _ -> None)
                |> Seq.toArray

            let children = Dictionary<int, ResizeArray<int>>()

            for pid, ppid in stats do
                match children.TryGetValue ppid with
                | true, values -> values.Add pid
                | _ -> children[ppid] <- ResizeArray [ pid ]

            let descendants = ResizeArray<int>()

            let rec visit pid =
                descendants.Add pid

                match children.TryGetValue pid with
                | true, values ->
                    for child in values do
                        visit child
                | _ -> ()

            visit rootPid

            let processes =
                descendants
                |> Seq.map (fun pid ->
                    let ppid = stats |> Array.find (fun (candidate, _) -> candidate = pid) |> snd
                    let rss = procRss pid
                    known.Add pid |> ignore
                    pid, ppid, rss)
                |> Seq.toArray

            samples.Add
                { TimestampMilliseconds = started.ElapsedMilliseconds
                  AggregateRssBytes = processes |> Array.sumBy (fun (_, _, rss) -> rss)
                  Processes = processes }
        with error ->
            fault <- Some error.Message

    sample ()

    let worker =
        Task.Run(fun () ->
            while not cancellation.IsCancellationRequested && fault.IsNone do
                Thread.Sleep 10
                sample ())

    { Stop =
        fun () ->
            cancellation.Cancel()
            worker.Wait()
            samples.ToArray(), fault, known |> Seq.toArray }

let rec discoverTree roots =
    let all =
        Directory.EnumerateDirectories("/proc")
        |> Seq.choose (fun path ->
            match Int32.TryParse(Path.GetFileName path) with
            | true, pid ->
                try
                    Some(pid, procStat pid)
                with _ ->
                    None
            | false, _ -> None)
        |> Seq.toArray

    let children = all |> Array.groupBy snd |> dict
    let found = HashSet<int>()

    let rec visit pid =
        if found.Add pid then
            match children.TryGetValue pid with
            | true, values ->
                for child, _ in values do
                    visit child
            | _ -> ()

    for root in roots do
        if Directory.Exists($"/proc/{root}") then
            visit root

    found |> Seq.toArray

let terminate pid =
    if Directory.Exists($"/proc/{pid}") then
        let code, _, _ =
            runCommand "/run/current-system/sw/bin/kill" [ "-TERM"; string pid ]

        if code <> 0 && Directory.Exists($"/proc/{pid}") then
            fail $"Could not terminate measured child {pid}."

let kill pid =
    if Directory.Exists($"/proc/{pid}") then
        let code, _, _ =
            runCommand "/run/current-system/sw/bin/kill" [ "-KILL"; string pid ]

        if code <> 0 && Directory.Exists($"/proc/{pid}") then
            fail $"Could not kill measured child {pid}."

let reap (root: Process) (known: int array) =
    let initial = Array.append known (discoverTree [| root.Id |]) |> Array.distinct

    let depth pid =
        let rec count value =
            if value = root.Id then
                0
            elif Directory.Exists($"/proc/{value}") then
                count (procStat value) + 1
            else
                0

        try
            count pid
        with _ ->
            0

    initial |> Array.sortByDescending depth |> Array.iter terminate
    Thread.Sleep 500

    let remaining =
        initial |> Array.filter (fun pid -> Directory.Exists($"/proc/{pid}"))

    remaining |> Array.sortByDescending depth |> Array.iter kill

    if not root.HasExited then
        root.WaitForExit(5000) |> ignore

    Thread.Sleep 100
    let residue = initial |> Array.filter (fun pid -> Directory.Exists($"/proc/{pid}"))
    initial, residue

let initialize =
    map
        [ "protocolVersion", map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 4L ]
          "clientInfo", map [ ("name", RpcValue.String "reference-performance") ]
          "capabilities",
          RpcValue.array
              [ RpcValue.String "workspace.root"
                RpcValue.String "workspace.children"
                RpcValue.String "workspace.delta"
                RpcValue.String "workspace.export" ]
          "limits",
          map
              [ "maxFrameBytes", RpcValue.Integer(int64 RpcCodec.secureLimits.MaximumValueBytes)
                "maxPageSize", RpcValue.Integer 500L ] ]

let nodeFields value =
    stringField "id" value, stringField "kind" value, stringField "name" value

let writeMetadata project =
    let original = File.ReadAllText project

    let marker =
        "<Compile Include=\"Items/N0001.cs\"><T024Performance>qualified</T024Performance></Compile>"

    require
        (original.Contains("<Compile Include=\"Items/N0001.cs\" />", StringComparison.Ordinal))
        "P0001 is missing its explicit N0001 compile item."

    let replacement =
        original.Replace("<Compile Include=\"Items/N0001.cs\" />", marker, StringComparison.Ordinal)

    let temporary = project + ".t024.tmp"

    use stream =
        new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)

    use writer = new StreamWriter(stream, new UTF8Encoding(false))
    writer.Write(replacement)
    writer.Flush()
    stream.Flush(true)
    writer.Close()
    stream.Close()
    File.Move(temporary, project, true)

let median values =
    let sorted = values |> Array.sort
    require (sorted.Length = 5) "Exactly five measured values are required for a median."
    sorted[2]

let requireNoError label error =
    match error with
    | None -> ()
    | Some value -> fail $"{label} failed: {value.Code}: {value.Message}"

let runScenario name measuredRun =
    let runDirectory = Path.Combine(corpusRoot, name)
    let solution = writeCorpus runDirectory
    let start = ProcessStartInfo(apphost)
    start.WorkingDirectory <- repositoryRoot
    start.UseShellExecute <- false
    start.RedirectStandardInput <- true
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.CreateNoWindow <- true
    start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
    start.Environment["NUGET_XMLDOC_MODE"] <- "skip"
    start.ArgumentList.Add("solution")
    start.ArgumentList.Add(solution)
    start.ArgumentList.Add("--pipe")
    use child = new Process(StartInfo = start)
    let mutable known = [||]
    let mutable sampler: Sampler option = None
    let mutable cleanupEvidence: obj = null

    try
        let rootStart = Stopwatch.GetTimestamp()
        require (child.Start()) "Could not start the Release apphost."
        sampler <- Some(startSampler child.Id)
        send child (request 1u "initialize" initialize)

        let initializeError, _ =
            readFrame child.StandardOutput.BaseStream 30000 |> response 1u

        requireNoError "initialize" initializeError
        send child (request 2u "workspace/root" RpcValue.emptyMap)

        let rootError, rootResult =
            readFrame child.StandardOutput.BaseStream 30000 |> response 2u

        requireNoError "workspace/root" rootError

        let rootMilliseconds =
            float (Stopwatch.GetTimestamp() - rootStart) * 1000.0
            / float Stopwatch.Frequency

        let mutable revision = integerField "revision" rootResult

        let projects =
            arrayField "nodes" rootResult
            |> Seq.map nodeFields
            |> Seq.filter (fun (_, kind, _) -> kind = "project")
            |> Seq.map (fun (id, _, name) -> name, id)
            |> Map.ofSeq

        require (projects.Count = 500) "workspace/root did not expose exactly 500 projects."

        let projectId =
            projects
            |> Map.tryFind "P0001"
            |> Option.defaultWith (fun () -> fail "workspace/root did not expose P0001.")

        let mutable requestId = 3u
        let materialized = Dictionary<string, string>(StringComparer.Ordinal)

        let readPage id =
            let mutable result: (RpcError option * RpcValue) option = None

            while result.IsNone do
                match readFrame child.StandardOutput.BaseStream 30000 with
                | RpcFrame.Response(actual, error, value) when actual = id ->
                    result <- Some(error, value)
                | RpcFrame.Notification("workspace/delta", _)
                | RpcFrame.Notification("workspace/reset", _) -> ()
                | frame -> fail $"Expected workspace/children response {id}, got {frame}."

            result.Value

        let pageProject projectName parentId =
            let mutable continuation: string option = None
            let mutable paging = true
            let mutable itemCount = 0

            while paging do
                let values =
                    ResizeArray
                        [ "parentId", RpcValue.String parentId; "pageSize", RpcValue.Integer 500L ]

                continuation
                |> Option.iter (fun token -> values.Add("continuationToken", RpcValue.String token))

                send child (request requestId "workspace/children" (map values))
                let pageError, page = readPage requestId
                requireNoError "workspace/children" pageError

                for node in arrayField "nodes" page do
                    let id, kind, nodeName = nodeFields node

                    if kind = "projectItem" then
                        require
                            (materialized.TryAdd(id, projectName + "|" + nodeName))
                            $"Duplicate materialized project-item id {id}."

                        itemCount <- itemCount + 1

                continuation <-
                    match RpcValue.tryField "nextToken" page with
                    | Some(RpcValue.String token) -> Some token
                    | Some RpcValue.Nil
                    | None -> None
                    | Some _ -> fail "workspace/children returned an invalid continuation token."

                requestId <- requestId + 1u
                paging <- continuation.IsSome

            require
                (itemCount = 500)
                $"{projectName} did not materialize exactly 500 project items."

        pageProject "P0001" projectId

        let itemId =
            materialized
            |> Seq.tryFind (fun pair -> pair.Value = "P0001|Compile: Items/N0001.cs")
            |> Option.map _.Key
            |> Option.defaultWith (fun () ->
                fail "P0001 did not materialize Compile: Items/N0001.cs.")

        for projectNumber in 2..500 do
            let projectName = $"P{projectNumber:D4}"
            pageProject projectName projects[projectName]

        require
            (materialized.Count = 250000)
            "The fully materialized public graph does not contain 250000 project items."

        let expectedMaterialized =
            [ for projectNumber in 1..500 do
                  for itemNumber in 1..500 do
                      yield $"P{projectNumber:D4}|Compile: Items/N{itemNumber:D4}.cs" ]
            |> Set.ofList

        require
            ((materialized.Values |> Set.ofSeq) = expectedMaterialized)
            "The materialized public graph does not contain every expected generated identity exactly once."

        let project = Path.Combine(runDirectory, "src", "P0001", "P0001.csproj")
        writeMetadata project
        let changeStart = Stopwatch.GetTimestamp()
        let mutable changeMilliseconds: float option = None
        let mutable changeRevision = revision

        while changeMilliseconds.IsNone do
            match readFrame child.StandardOutput.BaseStream 60000 with
            | RpcFrame.Notification("workspace/delta", parameters) ->
                let next = integerField "newRevision" parameters
                require (next > revision) "workspace/delta did not have a higher revision."

                let matches =
                    arrayField "changes" parameters
                    |> Seq.exists (fun change ->
                        let parentMatches =
                            RpcValue.tryField "parentId" change = Some(RpcValue.String projectId)

                        let oldMatches =
                            RpcValue.tryField "oldId" change = Some(RpcValue.String itemId)

                        match RpcValue.tryField "node" change with
                        | Some node ->
                            parentMatches
                            && stringField "kind" node = "projectItem"
                            && stringField "name" node = "Compile: Items/N0001.cs"
                            && (stringField "id" node = itemId || oldMatches)
                        | None -> false)

                revision <- next

                if matches then
                    changeRevision <- next

                    changeMilliseconds <-
                        Some(
                            float (Stopwatch.GetTimestamp() - changeStart) * 1000.0
                            / float Stopwatch.Frequency
                        )
            | RpcFrame.Notification("workspace/reset", parameters) ->
                revision <- integerField "revision" parameters
            | frame -> fail $"Expected a watcher delta after the atomic metadata edit, got {frame}."

        let exportStart = Stopwatch.GetTimestamp()
        send child (request requestId "workspace/export" RpcValue.emptyMap)

        let exportError, exportResult =
            readFrame child.StandardOutput.BaseStream 60000 |> response requestId

        requireNoError "workspace/export" exportError
        let operationId = stringField "operationId" exportResult
        let exportRevision = integerField "revision" exportResult
        require (exportRevision >= changeRevision) "workspace/export returned a stale revision."
        let ids = HashSet<string>(StringComparer.Ordinal)
        let expected = HashSet<string>(StringComparer.Ordinal)

        for projectNumber in 1..500 do
            for itemNumber in 1..500 do
                expected.Add($"P{projectNumber:D4}|Compile: Items/N{itemNumber:D4}.cs")
                |> ignore

        let observed = HashSet<string>(StringComparer.Ordinal)
        let kinds = Dictionary<string, int>(StringComparer.Ordinal)
        let mutable sequence = 0L
        let mutable finals = 0
        let mutable finalSeen = false
        let mutable completed = 0
        let mutable total = 0

        while completed = 0 do
            match readFrame child.StandardOutput.BaseStream 180000 with
            | RpcFrame.Notification("workspace/exportChunk", parameters) ->
                require (not finalSeen) "workspace/export emitted a chunk after its final chunk."

                require
                    (stringField "operationId" parameters = operationId)
                    "workspace/exportChunk operationId mismatch."

                require
                    (integerField "revision" parameters = exportRevision)
                    "workspace/exportChunk revision mismatch."

                require
                    (integerField "sequence" parameters = sequence)
                    "workspace/exportChunk sequence is not strictly ordered."

                sequence <- sequence + 1L

                if field "last" parameters = RpcValue.Boolean true then
                    finals <- finals + 1
                    finalSeen <- true

                for node in arrayField "nodes" parameters do
                    let id, kind, nodeName = nodeFields node
                    require (ids.Add id) $"workspace/export repeated public node id {id}."
                    total <- total + 1

                    kinds[kind] <-
                        (match kinds.TryGetValue kind with
                         | true, count -> count + 1
                         | _ -> 1)

                    if kind = "projectItem" then
                        let identity =
                            materialized.TryGetValue id
                            |> function
                                | true, knownIdentity -> knownIdentity
                                | false, _ ->
                                    fail
                                        $"workspace/export contained an unmaterialized project-item id {id}."

                        require
                            (observed.Add identity)
                            $"workspace/export repeated generated identity {identity}."

            | RpcFrame.Notification("operation/completed", parameters) ->
                require finalSeen "operation/completed arrived before the final export chunk."

                require
                    (integerField "sequence" parameters = sequence)
                    "operation/completed sequence did not follow the ordered export chunks."

                require
                    (stringField "operationId" parameters = operationId)
                    "operation/completed operationId mismatch."

                require
                    (integerField "revision" parameters = exportRevision)
                    "operation/completed revision mismatch."

                require
                    (stringField "outcome" parameters = "succeeded")
                    "workspace/export did not complete successfully."

                completed <- completed + 1
            | frame -> fail $"Unexpected export frame: {frame}"

        require
            (finals = 1)
            $"workspace/export produced {finals} final chunks instead of exactly one."

        require (completed = 1) "workspace/export did not produce exactly one completion."

        let projectItems =
            match kinds.TryGetValue "projectItem" with
            | true, count -> count
            | _ -> 0

        require
            (projectItems = 250000)
            $"workspace/export reported {projectItems} project items instead of 250000."

        require
            (observed.Count = expected.Count)
            "Generated project-item identity inventory is incomplete."

        for identity in expected do
            require
                (observed.Contains identity)
                $"workspace/export omitted generated identity {identity}."

        let exportMilliseconds =
            float (Stopwatch.GetTimestamp() - exportStart) * 1000.0
            / float Stopwatch.Frequency

        send child (request (requestId + 1u) "shutdown" RpcValue.emptyMap)

        let shutdownError, shutdown =
            readFrame child.StandardOutput.BaseStream 30000 |> response (requestId + 1u)

        requireNoError "shutdown" shutdownError
        require (field "accepted" shutdown = RpcValue.Boolean true) "shutdown was not accepted."
        child.StandardInput.Close()
        require (child.WaitForExit 30000) "The apphost did not exit after shutdown."
        require (child.ExitCode = 0) $"The apphost exited with {child.ExitCode}."
        let samples, samplerFault, sampled = sampler.Value.Stop()
        known <- sampled
        require samplerFault.IsNone (samplerFault |> Option.defaultValue "RSS sampler failed.")
        let peak = samples |> Array.maxBy _.AggregateRssBytes
        maxAggregateRss <- max maxAggregateRss peak.AggregateRssBytes
        totalNodes <- total
        generatedObserved <- projectItems
        generatedUnique <- observed.Count
        publicKinds <- kinds

        let run =
            dict
                [ "name", box name
                  "rootMilliseconds", box rootMilliseconds
                  "materializedChangeMilliseconds", box changeMilliseconds.Value
                  "exportMilliseconds", box exportMilliseconds
                  "effectiveDebounceMilliseconds", box 0
                  "rootRevision", box (integerField "revision" rootResult)
                  "changeRevision", box changeRevision
                  "exportRevision", box exportRevision
                  "totalPublicNodes", box total
                  "publicKindCounts", box kinds
                  "generatedExpected", box expected.Count
                  "generatedObserved", box projectItems
                  "generatedUnique", box observed.Count
                  "peakAggregateRssBytes", box peak.AggregateRssBytes
                  "peakSample",
                  box (
                      dict
                          [ "timestampMilliseconds", box peak.TimestampMilliseconds
                            "processes",
                            box (
                                peak.Processes
                                |> Array.map (fun (pid, ppid, rss) ->
                                    dict
                                        [ "pid", box pid
                                          "parentPid", box ppid
                                          "rssBytes", box rss ])
                            ) ]
                  ) ]

        run :> obj
    finally
        match sampler with
        | Some value ->
            let _, _, sampled = value.Stop()
            known <- Array.append known sampled |> Array.distinct
        | None -> ()

        let reaped, residue = reap child known

        cleanupEvidence <-
            dict
                [ "knownPids", box reaped
                  "residualPids", box residue
                  "processExited", box child.HasExited ]
            :> obj

        let residualText = String.Join(",", residue)
        require (residue.Length = 0) $"Measured child residue remained: {residualText}."

        if Directory.Exists(runDirectory) then
            Directory.Delete(runDirectory, true)

let runQualification () =
    require
        (not (File.Exists summaryPath))
        $"Refusing to overwrite the existing qualification summary: {summaryPath}"

    let failures = hostGate ()

    if not failures.IsEmpty then
        status <- "non-qualifying"
        reason <- String.Join("; ", failures)
    else
        try
            let warm = runScenario "warmup" false
            warmup <- warm

            for index in 1..5 do
                measured.Add(runScenario $"measured-{index:D2}" true)

            let timing key =
                measured
                |> Seq.map (fun run ->
                    let map = run :?> IDictionary<string, obj> in unbox<float> map[key])
                |> Seq.toArray

            medianRoot <- Some(median (timing "rootMilliseconds"))
            medianChange <- Some(median (timing "materializedChangeMilliseconds"))
            medianExport <- Some(median (timing "exportMilliseconds"))
            let rssBudget = int64 (1.5 * 1024.0 * 1024.0 * 1024.0)

            let underRss =
                measured
                |> Seq.forall (fun run ->
                    let map = run :?> IDictionary<string, obj> in
                    unbox<int64> map["peakAggregateRssBytes"] < rssBudget)

            let cardinality =
                generatedObserved = generatedExpected && generatedUnique = generatedExpected

            if
                medianRoot.Value <= 2000.0
                && medianChange.Value <= 500.0
                && medianExport.Value <= 30000.0
                && underRss
                && cardinality
            then
                status <- "qualifying"
                reason <- "all reference thresholds and invariants passed"
            else
                status <- "non-qualifying"
                reason <- "one or more timing, RSS, or cardinality thresholds failed"
        with error ->
            status <- "non-qualifying"
            reason <- error.Message

    cleanupCorpus ()
    cleanup <- "generated corpus removed; every measured child was reaped"
    writeSummary ()

    if status <> "qualifying" then
        fail $"Performance qualification is {status}: {reason}"

if requestedConfiguration <> [ "--configuration"; "Release" ] then
    fail "Usage: dotnet fsi scripts/qualify-performance.fsx --configuration Release"

try
    runQualification ()
with error ->
    try
        cleanupCorpus ()
        cleanup <- "generated corpus removed after failure"

        if not summaryWritten then
            writeSummary ()
    with summaryError ->
        eprintfn "Could not write performance summary: %s" summaryError.Message

    raise error
