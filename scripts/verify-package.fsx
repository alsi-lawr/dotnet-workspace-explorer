open System
open System.Buffers.Binary
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Text
open System.Threading.Tasks
open System.Xml.Linq

let fail message =
    raise (InvalidOperationException message)

let require condition message =
    if not condition then
        fail message

let scriptPath = __SOURCE_DIRECTORY__
let repositoryRoot = Path.GetFullPath(Path.Combine(scriptPath, ".."))

let configuration =
    match fsi.CommandLineArgs |> Array.skip 1 |> Array.toList with
    | [ "--configuration"; "Release" ] -> "Release"
    | _ -> fail "Usage: dotnet fsi scripts/verify-package.fsx --configuration Release"

let run directory executable (arguments: string list) =
    let start = ProcessStartInfo executable
    start.WorkingDirectory <- directory
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.CreateNoWindow <- true

    for argument in arguments do
        start.ArgumentList.Add argument

    use child = new Process(StartInfo = start)
    require (child.Start()) $"Could not start {executable}."
    let output = child.StandardOutput.ReadToEndAsync()
    let error = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    child.ExitCode, output.Result, error.Result

let requireSuccess label directory executable arguments =
    let exitCode, output, error = run directory executable arguments

    if exitCode <> 0 then
        fail $"{label} failed ({exitCode}).\nstdout:\n{output}\nstderr:\n{error}"

    output, error

let readEntry (entry: ZipArchiveEntry) =
    use stream = entry.Open()
    use reader = new StreamReader(stream, Encoding.UTF8, true)
    reader.ReadToEnd()

let normalizeArchivePath (path: string) =
    path.Replace('\\', '/').ToLowerInvariant()

let inspectPackage (packagePath: string) (packageId: string) (version: string) =
    use archive = ZipFile.OpenRead packagePath
    let entries = archive.Entries |> Seq.map (fun entry -> entry.FullName) |> Seq.toList
    let normalized = entries |> List.map normalizeArchivePath
    let contains path = normalized |> List.contains path

    let required =
        [ "readme.md"
          "tools/net10.0/any/dotnettoolsettings.xml"
          "tools/net10.0/any/dotnet.cli.plus.dll"
          "tools/net10.0/any/dotnet.cli.plus.deps.json"
          "tools/net10.0/any/dotnet.cli.plus.runtimeconfig.json"
          "tools/net10.0/any/dotnet.cli.plus.broker.dll"
          "tools/net10.0/any/dotnet.cli.plus.core.dll"
          "tools/net10.0/any/dotnet.cli.plus.msbuild.dll"
          "tools/net10.0/any/dotnet.cli.plus.solution.dll"
          "tools/net10.0/any/dotnet.cli.plus.transport.dll"
          "tools/net10.0/any/dotnet.cli.plus.workspace.dll"
          "tools/net10.0/any/fsharp.core.dll"
          "tools/net10.0/any/messagepack.dll"
          "tools/net10.0/any/messagepack.annotations.dll"
          "tools/net10.0/any/microsoft.build.locator.dll"
          "tools/net10.0/any/microsoft.visualstudio.solutionpersistence.dll" ]

    for path in required do
        require (contains path) $"The package is missing required runtime content: {path}"

    let isForbiddenRepositoryContent (path: string) =
        let fileName = Path.GetFileName path
        let extension = Path.GetExtension path
        let segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)

        let hasSegment values =
            segments |> Array.exists (fun segment -> values |> List.contains segment)

        let isSourceOrProject =
            [ ".cs"
              ".fs"
              ".fsx"
              ".vb"
              ".csproj"
              ".fsproj"
              ".vbproj"
              ".sln"
              ".slnx"
              ".slnf" ]
            |> List.contains extension

        isSourceOrProject
        || fileName.Contains("test", StringComparison.Ordinal)
        || hasSegment
            [ "src"
              "source"
              "script"
              "scripts"
              "project"
              "projects"
              "test"
              "tests"
              "fixture"
              "fixtures"
              "conformance"
              "generated"
              "docs"
              ".agent-workspace"
              "nvim"
              "casefile" ]

    let isNuGetMetadata (path: string) =
        path = "[content_types].xml"
        || path = "_rels/.rels"
        || path = $"{packageId.ToLowerInvariant()}.nuspec"
        || path.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal)
           && path.EndsWith(".psmdcp", StringComparison.Ordinal)

    let isRuntimeOrToolFile (path: string) =
        let prefix = "tools/net10.0/any/"
        let extension = Path.GetExtension path

        path.StartsWith(prefix, StringComparison.Ordinal)
        && path.Length > prefix.Length
        && [ ".dll"; ".pdb"; ".json"; ".xml"; ".so"; ".dylib"; ".exe" ]
           |> List.contains extension

    for path in normalized do
        require
            (not (isForbiddenRepositoryContent path))
            $"The package contains forbidden repository content: {path}"

        require
            (path = "readme.md" || isNuGetMetadata path || isRuntimeOrToolFile path)
            $"The package entry is outside the allowed release shape: {path}"

    let nuspec =
        archive.Entries
        |> Seq.tryFind (fun entry ->
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
        |> Option.defaultWith (fun () -> fail "The package does not contain a nuspec.")

    let document = XDocument.Parse(readEntry nuspec)

    let element name =
        document.Descendants()
        |> Seq.tryFind (fun item -> item.Name.LocalName = name)
        |> Option.map _.Value

    require
        (element "id" = Some packageId)
        "The nuspec package ID differs from the packed identity."

    require
        (element "version" = Some version)
        "The nuspec version differs from the packed identity."

    require (element "readme" = Some "README.md") "The nuspec does not declare the root README."

    let packageTypes =
        document.Descendants()
        |> Seq.filter (fun entry -> entry.Name.LocalName = "packageType")
        |> Seq.choose (fun entry ->
            entry.Attributes()
            |> Seq.tryFind (fun attribute -> attribute.Name.LocalName = "name")
            |> Option.map _.Value)
        |> Seq.toList

    require
        (packageTypes
         |> List.exists (fun value ->
             String.Equals(value, "DotnetTool", StringComparison.OrdinalIgnoreCase)))
        "The package is not a DotnetTool package."

    printfn "package: %s" packagePath

    printfn
        "manifest: %d entries; archive shape limited to root README, NuGet metadata, and approved tool/runtime files"
        entries.Length

let deleteDirectory path =
    if Directory.Exists path then
        Directory.Delete(path, true)

type Value =
    | Nil
    | Boolean of bool
    | Integer of int64
    | Text of string
    | Array of Value list
    | Map of Map<string, Value>
    | Other

let bytesForUnsigned value =
    if value <= 127UL then
        [| byte value |]
    elif value <= 255UL then
        [| 0xccuy; byte value |]
    elif value <= 65535UL then
        let bytes = Array.zeroCreate 3
        bytes[0] <- 0xcduy
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan 1, uint16 value)
        bytes
    else
        let bytes = Array.zeroCreate 5
        bytes[0] <- 0xceuy
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan 1, uint32 value)
        bytes

let stringBytes (value: string) =
    let content = Encoding.UTF8.GetBytes value

    if content.Length <= 31 then
        Array.concat [ [| 0xa0uy + byte content.Length |]; content ]
    elif content.Length <= 255 then
        Array.concat [ [| 0xd9uy; byte content.Length |]; content ]
    else
        let header = Array.zeroCreate 3
        header[0] <- 0xdauy
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan 1, uint16 content.Length)
        Array.concat [ header; content ]

let rec encode value =
    match value with
    | Nil -> [| 0xc0uy |]
    | Boolean false -> [| 0xc2uy |]
    | Boolean true -> [| 0xc3uy |]
    | Integer number when number >= 0L -> bytesForUnsigned (uint64 number)
    | Integer _ -> fail "The smoke protocol encoder only sends non-negative integers."
    | Text text -> stringBytes text
    | Array values ->
        require (values.Length <= 15) "The smoke protocol encoder only supports small arrays."
        Array.concat ([| 0x90uy + byte values.Length |] :: (values |> List.map encode))
    | Map fields ->
        require (fields.Count <= 15) "The smoke protocol encoder only supports small maps."

        fields
        |> Map.toList
        |> List.collect (fun (key, value) -> [ stringBytes key; encode value ])
        |> fun contents -> Array.concat ([| 0x80uy + byte fields.Count |] :: contents)
    | Other -> fail "The smoke protocol encoder cannot send this value."

let map fields = Map(Map.ofList fields)

let readUInt16 bytes index =
    BinaryPrimitives.ReadUInt16BigEndian(ReadOnlySpan(bytes, index, 2))

let readUInt32 bytes index =
    BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan(bytes, index, 4))

let readUInt64 bytes index =
    BinaryPrimitives.ReadUInt64BigEndian(ReadOnlySpan(bytes, index, 8))

let requireAvailable (bytes: byte array) index count =
    if index + count > bytes.Length then
        raise (EndOfStreamException())

let rec decode bytes index =
    requireAvailable bytes index 1
    let marker = bytes[index]

    let text count offset =
        requireAvailable bytes offset count
        Text(Encoding.UTF8.GetString(bytes, offset, count)), offset + count

    match marker with
    | value when value <= 0x7fuy -> Integer(int64 value), index + 1
    | value when value >= 0xe0uy -> Integer(int64 (sbyte value)), index + 1
    | value when value >= 0xa0uy && value <= 0xbfuy -> text (int (value &&& 0x1fuy)) (index + 1)
    | value when value >= 0x90uy && value <= 0x9fuy ->
        decodeArray bytes (int (value &&& 0x0fuy)) (index + 1)
    | value when value >= 0x80uy && value <= 0x8fuy ->
        decodeMap bytes (int (value &&& 0x0fuy)) (index + 1)
    | 0xc0uy -> Nil, index + 1
    | 0xc2uy -> Boolean false, index + 1
    | 0xc3uy -> Boolean true, index + 1
    | 0xccuy ->
        requireAvailable bytes (index + 1) 1
        Integer(int64 bytes[index + 1]), index + 2
    | 0xcduy ->
        requireAvailable bytes (index + 1) 2
        Integer(int64 (readUInt16 bytes (index + 1))), index + 3
    | 0xceuy ->
        requireAvailable bytes (index + 1) 4
        Integer(int64 (readUInt32 bytes (index + 1))), index + 5
    | 0xcfuy ->
        requireAvailable bytes (index + 1) 8
        Integer(int64 (readUInt64 bytes (index + 1))), index + 9
    | 0xd0uy ->
        requireAvailable bytes (index + 1) 1
        Integer(int64 (sbyte bytes[index + 1])), index + 2
    | 0xd1uy ->
        requireAvailable bytes (index + 1) 2
        Integer(int64 (int16 (readUInt16 bytes (index + 1)))), index + 3
    | 0xd2uy ->
        requireAvailable bytes (index + 1) 4
        Integer(int64 (int32 (readUInt32 bytes (index + 1)))), index + 5
    | 0xd3uy ->
        requireAvailable bytes (index + 1) 8
        Integer(int64 (readUInt64 bytes (index + 1))), index + 9
    | 0xd9uy ->
        requireAvailable bytes (index + 1) 1
        text (int bytes[index + 1]) (index + 2)
    | 0xdauy ->
        requireAvailable bytes (index + 1) 2
        text (int (readUInt16 bytes (index + 1))) (index + 3)
    | 0xdbuy ->
        requireAvailable bytes (index + 1) 4
        text (int (readUInt32 bytes (index + 1))) (index + 5)
    | 0xdcuy ->
        requireAvailable bytes (index + 1) 2
        decodeArray bytes (int (readUInt16 bytes (index + 1))) (index + 3)
    | 0xdduy ->
        requireAvailable bytes (index + 1) 4
        decodeArray bytes (int (readUInt32 bytes (index + 1))) (index + 5)
    | 0xdeuy ->
        requireAvailable bytes (index + 1) 2
        decodeMap bytes (int (readUInt16 bytes (index + 1))) (index + 3)
    | 0xdfuy ->
        requireAvailable bytes (index + 1) 4
        decodeMap bytes (int (readUInt32 bytes (index + 1))) (index + 5)
    | _ -> fail $"Unsupported MessagePack marker 0x{marker:x2} in smoke response."

and decodeArray bytes count index =
    let mutable cursor = index
    let values = ResizeArray<Value>()

    for _ in 1..count do
        let value, next = decode bytes cursor
        values.Add value
        cursor <- next

    Array(values |> Seq.toList), cursor

and decodeMap bytes count index =
    let mutable cursor = index
    let mutable fields = Map.empty

    for _ in 1..count do
        let key, afterKey = decode bytes cursor
        let value, next = decode bytes afterKey

        match key with
        | Text name -> fields <- fields.Add(name, value)
        | _ -> fail "The smoke response contains a non-string map key."

        cursor <- next

    Map fields, cursor

let readFrame (stream: Stream) =
    let pending = ResizeArray<byte>()
    let mutable frame = None

    while frame.IsNone do
        let next = stream.ReadByte()
        require (next >= 0) "The pipe ended before a complete MessagePack response arrived."
        pending.Add(byte next)

        try
            let value, consumed = decode (pending.ToArray()) 0
            require (consumed = pending.Count) "The pipe response has trailing bytes."
            frame <- Some value
        with :? EndOfStreamException ->
            ()

    frame.Value

let expectResponse expectedId =
    function
    | Array [ Integer 1L; Integer actualId; Nil; Map result ] when actualId = expectedId -> result
    | value -> fail $"Expected successful response {expectedId}, got {value}."

let readFrameWithin (timeout: int) stream =
    let reading = Task.Run(fun () -> readFrame stream)

    if not (reading.Wait timeout) then
        fail "The installed pipe tool timed out while responding."

    reading.Result

let field name fields =
    fields
    |> Map.tryFind name
    |> Option.defaultWith (fun () -> fail $"Response is missing '{name}'.")

let startPipe apphost target =
    let start = ProcessStartInfo apphost
    start.UseShellExecute <- false
    start.RedirectStandardInput <- true
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.CreateNoWindow <- true
    start.ArgumentList.Add "solution"
    start.ArgumentList.Add target
    start.ArgumentList.Add "--pipe"
    let child = new Process(StartInfo = start)
    require (child.Start()) "Could not start the installed tool pipe."
    child

let sendFrame (stream: Stream) value =
    let bytes = encode value
    stream.Write(bytes, 0, bytes.Length)
    stream.Flush()

let request id methodName parameters =
    Array [ Integer 0L; Integer id; Text methodName; parameters ]

let runPipeSmoke apphost solution =
    use child = startPipe apphost solution

    try
        let initialize =
            map
                [ "protocolVersion", map [ "major", Integer 1L; "minor", Integer 0L ]
                  "clientInfo", map (List.singleton ("name", Text "package-smoke"))
                  "capabilities", Array []
                  "limits", map [ "maxFrameBytes", Integer 65536L; "maxPageSize", Integer 16L ] ]

        sendFrame child.StandardInput.BaseStream (request 1L "initialize" initialize)

        let initialized =
            readFrameWithin 10000 child.StandardOutput.BaseStream |> expectResponse 1L

        match
            field "protocolVersion" initialized,
            field "serverInfo" initialized,
            field "workspace" initialized
        with
        | Map version, Map _, Map _ ->
            require
                (field "major" version = Integer 1L && field "minor" version = Integer 0L)
                "The server did not negotiate v1.0."
        | _ -> fail "The initialize response has an invalid public shape."

        sendFrame child.StandardInput.BaseStream (request 2L "workspace/root" (map []))

        let root =
            readFrameWithin 10000 child.StandardOutput.BaseStream |> expectResponse 2L

        match field "revision" root, field "nodes" root with
        | Integer revision, Array nodes ->
            require (revision >= 0L) "The workspace/root response has a negative revision."
            printfn "pipe: workspace/root revision %d with %d nodes" revision nodes.Length
        | _ -> fail "The workspace/root response has an invalid public shape."

        sendFrame child.StandardInput.BaseStream (request 3L "shutdown" (map []))

        let shutdown =
            readFrameWithin 10000 child.StandardOutput.BaseStream |> expectResponse 3L

        require (field "accepted" shutdown = Boolean true) "The shutdown response was not accepted."
        child.StandardInput.Close()
        require (child.WaitForExit 10000) "The installed pipe tool did not exit after shutdown."
        require (child.ExitCode = 0) $"The installed pipe tool exited with {child.ExitCode}."

        require
            (String.IsNullOrWhiteSpace(child.StandardError.ReadToEnd()))
            "The installed pipe tool wrote stderr."

        require
            (child.StandardOutput.BaseStream.ReadByte() = -1)
            "The installed pipe tool did not close stdout."

        printfn "pipe: initialize/root/shutdown responses validated"
    finally
        if not child.HasExited then
            child.Kill true
            child.WaitForExit()

let runRoot =
    Path.Combine(repositoryRoot, ".agent-workspace", "release", $"package-{Guid.NewGuid():N}")

let packageDirectory = Path.Combine(runRoot, "package")
let toolDirectory = Path.Combine(runRoot, "tool")
let fixtureDirectory = Path.Combine(runRoot, "fixture")
let nugetConfig = Path.Combine(runRoot, "NuGet.Config")
let packageId = "Dotnet.CLI.Plus"
let version = $"1.0.0-t022.{Guid.NewGuid():N}"

try
    Directory.CreateDirectory packageDirectory |> ignore
    Directory.CreateDirectory toolDirectory |> ignore
    Directory.CreateDirectory fixtureDirectory |> ignore

    File.WriteAllText(
        nugetConfig,
        "<configuration><packageSources><clear /></packageSources></configuration>"
    )

    let project =
        Path.Combine(repositoryRoot, "src", "Dotnet.CLI.Plus", "Dotnet.CLI.Plus.fsproj")

    requireSuccess
        "pack"
        repositoryRoot
        "dotnet"
        [ "pack"
          project
          "--configuration"
          configuration
          "--output"
          packageDirectory
          $"-p:PackageVersion={version}" ]
    |> ignore

    let packages = Directory.GetFiles(packageDirectory, "*.nupkg")
    require (packages.Length = 1) $"Expected exactly one fresh nupkg, found {packages.Length}."
    let packagePath = packages[0]

    require
        (Path.GetFileName packagePath = $"{packageId}.{version}.nupkg")
        "The fresh package filename differs from the generated identity."

    inspectPackage packagePath packageId version

    requireSuccess
        "isolated tool install"
        repositoryRoot
        "dotnet"
        [ "tool"
          "install"
          "--tool-path"
          toolDirectory
          "--configfile"
          nugetConfig
          "--add-source"
          packageDirectory
          packageId
          "--version"
          version ]
    |> ignore

    let executable =
        if OperatingSystem.IsWindows() then
            "dotnet-plus.exe"
        else
            "dotnet-plus"

    let apphost = Path.Combine(toolDirectory, executable)
    require (File.Exists apphost) "The exact installed package did not provide dotnet-plus."
    printfn "install: %s %s into %s" packageId version toolDirectory

    let solutionName = "PackageSmoke"

    requireSuccess
        "create solution"
        fixtureDirectory
        "dotnet"
        [ "new"; "sln"; "--format"; "slnx"; "--name"; solutionName ]
    |> ignore

    requireSuccess
        "create project"
        fixtureDirectory
        "dotnet"
        [ "new"
          "classlib"
          "--no-restore"
          "--framework"
          "net10.0"
          "--name"
          "SmokeProject"
          "--output"
          "SmokeProject" ]
    |> ignore

    let solution = Path.Combine(fixtureDirectory, solutionName + ".slnx")

    let projectPath =
        Path.Combine(fixtureDirectory, "SmokeProject", "SmokeProject.csproj")

    let directOutput, directError =
        requireSuccess
            "direct installed solution mutation"
            fixtureDirectory
            apphost
            [ "--json"; "solution"; solution; "add"; projectPath ]

    require (String.IsNullOrWhiteSpace directError) "The direct installed invocation wrote stderr."

    require
        (directOutput.Contains("\"success\":true", StringComparison.Ordinal))
        "The direct installed invocation did not report JSON success."

    require
        (File.ReadAllText(solution).Contains("SmokeProject.csproj", StringComparison.Ordinal))
        "The direct installed invocation did not mutate the solution."

    printfn "direct: installed solution mutation validated"

    runPipeSmoke apphost solution
finally
    deleteDirectory runRoot
    require (not (Directory.Exists runRoot)) "Package smoke cleanup left its run root behind."
    printfn "cleanup: %s removed" runRoot
