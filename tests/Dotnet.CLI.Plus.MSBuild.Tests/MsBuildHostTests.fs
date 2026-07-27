namespace Dotnet.CLI.Plus.MSBuild.Tests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open Dotnet.CLI.Plus.Transport
open FsUnit.Xunit
open Xunit

module private Test =
    let private pendingFrames = Dictionary<int, ResizeArray<byte>>()

    let temporaryDirectory name =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-msbuild-{name}-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        File.WriteAllText(Path.Combine(path, "Directory.Build.props"), "<Project />")
        path

    let fixturePath name =
        Path.Combine(AppContext.BaseDirectory, "ConformanceFixtures", "MSBuild", name)

    let copyFixture directory name =
        let source = fixturePath name
        let destination = Path.Combine(directory, name)

        for path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) do
            let target = Path.Combine(destination, Path.GetRelativePath(source, path))
            Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
            File.Copy(path, target)

        destination

    let rec private tryRepositoryRoot (directory: string) =
        if File.Exists(Path.Combine(directory, "Directory.Packages.props")) then
            Some directory
        else
            match Directory.GetParent directory with
            | null -> None
            | parent -> tryRepositoryRoot parent.FullName

    let repositoryRoot directory =
        [ directory; Directory.GetCurrentDirectory() ]
        |> Seq.choose tryRepositoryRoot
        |> Seq.tryHead
        |> Option.defaultWith (fun () -> failwith "Could not locate the repository root.")

    let configuration =
        let baseDirectory = DirectoryInfo AppContext.BaseDirectory

        match baseDirectory.Parent with
        | null -> failwith "Could not determine the build configuration."
        | parent when parent.Name = "Debug" || parent.Name = "Release" -> parent.Name
        | _ -> "Debug"

    let apphost =
        let name =
            if OperatingSystem.IsWindows() then
                "Dotnet.CLI.Plus.exe"
            else
                "Dotnet.CLI.Plus"

        Path.Combine(
            repositoryRoot AppContext.BaseDirectory,
            "src",
            "Dotnet.CLI.Plus",
            "bin",
            configuration,
            "net10.0",
            name
        )

    let write (path: string) (contents: string) = File.WriteAllText(path, contents)

    let simpleProject directory name extension =
        let project = Path.Combine(directory, name + extension)

        write
            project
            """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
</Project>
"""

        project

    let writeGlobalJson directory version =
        write
            (Path.Combine(directory, "global.json"))
            (sprintf
                """{"sdk":{"version":"%s","rollForward":"disable","allowPrerelease":false}}"""
                version)

    let writeSolution directory (projects: seq<string>) =
        let solution = Path.Combine(directory, "Demo.slnx")

        projects
        |> Seq.map (fun project -> $"  <Project Path=\"{Path.GetFileName project}\" />")
        |> String.concat Environment.NewLine
        |> fun entries ->
            $"<Solution>{Environment.NewLine}{entries}{Environment.NewLine}</Solution>"
        |> write solution

        solution

    let runDotnet workingDirectory argument =
        let start = ProcessStartInfo "dotnet"
        start.WorkingDirectory <- workingDirectory
        start.ArgumentList.Add argument
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.UseShellExecute <- false

        use child = Process.Start start
        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        child.WaitForExit()

        if child.ExitCode <> 0 then
            failwithf "dotnet %s failed with exit code %d: %s" argument child.ExitCode error

        output

    let currentSdkVersion workingDirectory =
        runDotnet workingDirectory "--version" |> _.Trim()

    let currentToolsetPath workingDirectory =
        runDotnet workingDirectory "--info"
        |> _.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.tryPick (fun line ->
            let prefix = "Base Path:"

            if line.StartsWith(prefix, StringComparison.Ordinal) then
                Some(line.Substring(prefix.Length).Trim() |> Path.TrimEndingDirectorySeparator)
            else
                None)
        |> Option.defaultWith (fun () ->
            failwith "dotnet --info did not report the selected SDK base path.")

    let start arguments =
        let start = ProcessStartInfo apphost

        for argument in arguments do
            start.ArgumentList.Add argument

        start.RedirectStandardInput <- true
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.UseShellExecute <- false
        start.CreateNoWindow <- true

        match Process.Start start with
        | null -> failwith "Could not start the built apphost."
        | child -> child

    let startWorker toolsetPath =
        start [ "internal"; "msbuild-host"; "--toolset"; toolsetPath ]

    let startPipe solution =
        start [ "solution"; solution; "--pipe" ]

    let disposeProcess (child: Process) =
        if not child.HasExited then
            child.Kill true
            child.WaitForExit()

        pendingFrames.Remove child.Id |> ignore
        child.Dispose()

    let request id name parameters =
        RpcCodec.encodeFrame (Request(id, name, parameters))

    let send (child: Process) id name parameters =
        let bytes = request id name parameters
        child.StandardInput.BaseStream.Write(bytes, 0, bytes.Length)
        child.StandardInput.BaseStream.Flush()

    let readFrame (child: Process) =
        let bytes =
            match pendingFrames.TryGetValue child.Id with
            | true, pending -> pending
            | false, _ ->
                let pending = ResizeArray<byte>()
                pendingFrames.Add(child.Id, pending)
                pending

        let mutable frame = None

        while frame.IsNone do
            match RpcCodec.tryReadValueLength RpcCodec.secureLimits (bytes.ToArray()) with
            | Error RpcDecodeError.Incomplete ->
                let buffer = Array.zeroCreate<byte> 8192
                let count = child.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)

                if count = 0 then
                    failwith "Apphost stdout ended before a complete frame."

                for index in 0 .. count - 1 do
                    bytes.Add buffer[index]
            | Error error -> failwithf "Invalid apphost frame: %A" error
            | Ok length ->
                let encoded = bytes.GetRange(0, length).ToArray()
                bytes.RemoveRange(0, length)

                match RpcCodec.decodeFrame RpcCodec.secureLimits encoded with
                | Ok(RpcFrameDecodeResult.Frame decoded) -> frame <- Some decoded
                | decoded -> failwithf "Invalid apphost frame: %A" decoded

        frame.Value

    let response expectedId =
        function
        | Response(id, error, result) when id = expectedId -> error, result
        | frame -> failwithf "Expected response %d, got %A" expectedId frame

    let field name value =
        value |> RpcValue.requireMap "value" |> RpcValue.requireField name

    let stringField name value =
        field name value |> RpcValue.requireString name

    let values name value =
        field name value |> RpcValue.requireArray name

    let strings name value =
        values name value |> Seq.map (RpcValue.requireString name)

    let requireSuccess id child =
        let error, result = readFrame child |> response id

        match error with
        | None -> result
        | Some failure -> failwithf "%s: %s" failure.Code failure.Message

    let requireSuccessAfterWorkspaceNotifications id expectedRevision child =
        let mutable revision = expectedRevision
        let mutable result = None

        while result.IsNone do
            match readFrame child with
            | Notification("workspace/delta", parameters) ->
                let baseRevision =
                    field "baseRevision" parameters |> RpcValue.requireInteger "baseRevision"

                let nextRevision =
                    field "newRevision" parameters |> RpcValue.requireInteger "newRevision"

                Assert.Equal(revision, baseRevision)
                Assert.True(nextRevision > baseRevision)
                revision <- nextRevision
            | Notification("workspace/reset", parameters) ->
                let nextRevision = field "revision" parameters |> RpcValue.requireInteger "revision"
                Assert.True(nextRevision > revision)
                revision <- nextRevision
            | Response(actual, error, value) when actual = id ->
                match error with
                | None -> result <- Some value
                | Some failure -> failwithf "%s: %s" failure.Code failure.Message
            | frame -> failwithf "Expected workspace notification or response %d, got %A" id frame

        result.Value, revision

    let readMatchingWorkspaceReset expectedRevision child =
        match readFrame child with
        | Notification("workspace/reset", parameters) ->
            Assert.Equal(
                expectedRevision,
                field "revision" parameters |> RpcValue.requireInteger "revision"
            )

            parameters
        | frame -> failwithf "Expected matching workspace reset, got %A" frame

    let workerInitializeVersion major minor frameLimit =
        RpcValue.map
            [ "profile", RpcValue.String "dotnet-cli-plus/msbuild"
              "protocolVersion",
              RpcValue.map [ "major", RpcValue.Integer major; "minor", RpcValue.Integer minor ]
              "limits", RpcValue.map [ "maxFrameBytes", RpcValue.Integer(int64 frameLimit) ] ]

    let workerInitialize frameLimit =
        workerInitializeVersion 2L 0L frameLimit

    let initializeWorker child id =
        send child id "initialize" (workerInitialize RpcCodec.secureLimits.MaximumValueBytes)
        requireSuccess id child |> ignore

    let pipeInitialize =
        RpcValue.map
            [ "protocolVersion",
              RpcValue.map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 0L ]
              "clientInfo", RpcValue.map [ "name", RpcValue.String "msbuild-contract-tests" ]
              "capabilities",
              RpcValue.array
                  [ RpcValue.String "workspace.root"
                    RpcValue.String "workspace.children"
                    RpcValue.String "workspace.delta"
                    RpcValue.String "workspace.refresh"
                    RpcValue.String "workspace.export"
                    RpcValue.String "operation.cancel" ]
              "limits",
              RpcValue.map
                  [ "maxFrameBytes", RpcValue.Integer 1048576L
                    "maxPageSize", RpcValue.Integer 100L ] ]

    let initializePipe child id =
        send child id "initialize" pipeInitialize
        requireSuccess id child |> ignore

    let evaluate child id project =
        send child id "msbuild/evaluate" (RpcValue.map [ "projectPath", RpcValue.String project ])
        readFrame child |> response id

    let invalidate child id paths =
        let parameters =
            RpcValue.map [ "paths", paths |> Seq.map RpcValue.String |> RpcValue.array ]

        send child id "msbuild/invalidate" parameters
        requireSuccess id child

    let shutdown child id =
        send child id "shutdown" RpcValue.emptyMap
        let result = requireSuccess id child
        Assert.Equal(Some(RpcValue.Boolean true), RpcValue.tryField "accepted" result)
        child.StandardInput.Close()
        Assert.True(child.WaitForExit 5000, "The apphost did not exit after shutdown.")
        Assert.Equal(-1, child.StandardOutput.BaseStream.ReadByte())
        child.ExitCode |> should equal 0
        Assert.Equal(String.Empty, child.StandardError.ReadToEnd())

    let withWorker directory action =
        let worker = startWorker (currentToolsetPath directory)

        try
            initializeWorker worker 1u
            action worker |> shutdown worker
        finally
            disposeProcess worker

    let withPipe solution action =
        let app = startPipe solution

        try
            initializePipe app 1u
            action app |> shutdown app
        finally
            disposeProcess app

    let hydrateProject child firstId =
        send child firstId "workspace/root" RpcValue.emptyMap
        let root = requireSuccess firstId child

        let rootRevision = field "revision" root |> RpcValue.requireInteger "revision"

        let projectId =
            values "nodes" root
            |> Seq.filter (fun node -> stringField "kind" node = "project")
            |> Seq.filter (fun node -> stringField "name" node = "Selected")
            |> Seq.map (stringField "id")
            |> Seq.exactlyOne

        let mutable continuation = None
        let mutable requestId = firstId + 1u
        let mutable hasMore = true
        let mutable targetFrameworkFound = false
        let mutable hydratedRevision = None

        while hasMore && not targetFrameworkFound do
            let parameters =
                [ "parentId", RpcValue.String projectId; "pageSize", RpcValue.Integer 100L ]
                |> fun fields ->
                    continuation
                    |> Option.map (fun token ->
                        ("continuationToken", RpcValue.String token) :: fields)
                    |> Option.defaultValue fields
                |> RpcValue.map

            send child requestId "workspace/children" parameters
            let page = requireSuccess requestId child

            if hydratedRevision.IsNone then
                match readFrame child with
                | Notification("workspace/delta", parameters) ->
                    Assert.Equal(
                        rootRevision,
                        field "baseRevision" parameters |> RpcValue.requireInteger "baseRevision"
                    )

                    let revision =
                        field "newRevision" parameters |> RpcValue.requireInteger "newRevision"

                    Assert.True(revision > rootRevision)
                    hydratedRevision <- Some revision
                | frame -> failwithf "Expected hydration delta, got %A" frame

            targetFrameworkFound <-
                values "nodes" page
                |> Seq.exists (fun node ->
                    stringField "kind" node = "projectItem"
                    && stringField "name" node = "Evaluated TargetFramework = net8.0")

            continuation <-
                match RpcValue.tryField "nextToken" page with
                | Some(RpcValue.String token) -> Some token
                | Some RpcValue.Nil
                | None -> None
                | Some value -> failwithf "Unexpected continuation token: %A" value

            hasMore <- continuation.IsSome
            requestId <- requestId + 1u

        Assert.True(
            targetFrameworkFound,
            "Fresh project paging did not expose Evaluated TargetFramework = net8.0."
        )

        hydratedRevision
        |> Option.defaultWith (fun () -> failwith "The hydration delta was not observed.")

type MsBuildHostTests() =
    [<Fact>]
    member _.``should retain CPM owner paths and exact item-group conditions``() =
        let directory = Test.temporaryDirectory "package-ownership"

        try
            let root = Path.Combine(directory, "Directory.Packages.props")
            let project = Path.Combine(directory, "App.csproj")

            Test.write
                root
                """
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup Condition=" '$(TargetFramework)' == 'net8.0' ">
    <PackageVersion Include="Example" Version="1.2.3" />
  </ItemGroup>
</Project>
"""

            Test.write
                project
                """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup Condition=" '$(TargetFramework)' == 'net8.0' ">
    <PackageReference Include="Example" />
  </ItemGroup>
</Project>
"""

            Test.withWorker directory (fun worker ->
                let error, snapshot = Test.evaluate worker 2u project
                error.IsNone |> should equal true

                let dimension =
                    Test.values "dimensions" snapshot
                    |> Seq.find (fun value ->
                        Test.field "targetFramework" value = RpcValue.String "net8.0")

                let membership = Test.values "packageMemberships" dimension |> Seq.exactlyOne
                let owner = Test.values "packageVersions" dimension |> Seq.exactlyOne
                Test.stringField "declaringPath" membership |> should equal project
                Test.stringField "declaringPath" owner |> should equal root

                Test.stringField "condition" membership
                |> should equal " '$(TargetFramework)' == 'net8.0' "

                Test.stringField "condition" owner
                |> should equal " '$(TargetFramework)' == 'net8.0' "

                3u)
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should project dimensions and invalidate imports and globs in the real worker``() =
        let directory = Test.temporaryDirectory "projection"

        try
            let projection = Test.copyFixture directory "Projection"
            let generatedDirectory = Path.Combine(projection, "Generated")
            let props = Path.Combine(projection, "Directory.Build.props")
            let project = Path.Combine(projection, "Project.csproj")

            Test.withWorker directory (fun worker ->
                let error, snapshot = Test.evaluate worker 2u project
                Assert.True error.IsNone
                let dimensions = Test.values "dimensions" snapshot
                Assert.Equal(3, dimensions.Length)

                let dimension framework =
                    dimensions
                    |> Seq.find (fun value ->
                        Test.field "targetFramework" value = RpcValue.String framework)

                let includes value =
                    Test.values "items" value |> Seq.map (Test.stringField "include")

                Assert.Contains("Eight.cs", dimension "net8.0" |> includes)
                Assert.DoesNotContain("Eight.cs", dimension "net9.0" |> includes)
                Assert.Contains(props, Test.strings "imports" snapshot)
                Assert.Contains(generatedDirectory, Test.strings "globRoots" snapshot)

                let importedProperties =
                    dimensions
                    |> Seq.collect (Test.values "properties")
                    |> Seq.filter (fun value -> Test.stringField "name" value = "ImportedProperty")
                    |> Seq.map (Test.stringField "value")

                Assert.Contains("before", importedProperties)

                let generated = Path.Combine(generatedDirectory, "New.cs")
                Test.write generated "class New {}"

                let globInvalidation = Test.invalidate worker 3u [ generated ]
                Assert.Contains(project, Test.strings "invalidatedProjects" globInvalidation)

                let _, withGenerated = Test.evaluate worker 4u project

                Assert.Contains(
                    withGenerated
                    |> Test.values "dimensions"
                    |> Seq.collect (Test.values "items")
                    |> Seq.map (Test.stringField "include"),
                    fun itemInclude ->
                        itemInclude
                            .Replace('\\', '/')
                            .EndsWith("Generated/New.cs", StringComparison.Ordinal)
                )

                Test.write
                    props
                    ("<Project><PropertyGroup>"
                     + "<ImportedProperty>after</ImportedProperty>"
                     + "</PropertyGroup></Project>")

                let importInvalidation = Test.invalidate worker 5u [ props ]
                Assert.Contains(project, Test.strings "invalidatedProjects" importInvalidation)
                let _, changed = Test.evaluate worker 6u project

                Assert.Contains(
                    changed |> Test.values "dimensions" |> Seq.collect (Test.values "properties"),
                    fun value ->
                        Test.stringField "name" value = "ImportedProperty"
                        && Test.stringField "value" value = "after"
                )

                7u)
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``should make managed projects writable and unknown projects read only``() =
        let directory = Test.temporaryDirectory "capabilities"

        try
            let unknown = Path.Combine(directory, "Unknown.proj")
            File.Copy(Test.fixturePath "Unknown.proj", unknown)

            let projects =
                [ Test.simpleProject directory "CSharp" ".csproj", "Full", true
                  unknown, "UnknownProjectSystem", false ]

            Test.withWorker directory (fun worker ->
                for index, (project, expectedProfile, expectedWrite) in Seq.indexed projects do
                    let error, snapshot = Test.evaluate worker (uint32 index + 2u) project
                    Assert.True error.IsNone
                    Assert.Equal(expectedProfile, Test.stringField "capabilityProfile" snapshot)
                    let capabilities = Test.strings "capabilities" snapshot |> Seq.toArray
                    Assert.Contains("workspace.read", capabilities)
                    Assert.Equal(expectedWrite, capabilities |> Array.contains "workspace.write")

                4u)
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``should reject incompatible private worker versions and shut down cleanly``() =
        let directory = Test.temporaryDirectory "protocol"

        try
            let worker = Test.startWorker (Test.currentToolsetPath directory)

            try
                let wrongProfile =
                    Test.workerInitialize 4096
                    |> function
                        | RpcValue.Map fields ->
                            fields.SetItem("profile", RpcValue.String "dotnet-cli-plus/workspace")
                            |> RpcValue.Map
                        | _ -> failwith "Initialize payload was not a map."

                Test.send worker 1u "initialize" wrongProfile
                let rejected, _ = Test.readFrame worker |> Test.response 1u
                Assert.Equal("invalid_params", rejected.Value.Code)

                Test.send worker 2u "initialize" (Test.workerInitializeVersion 1L 0L 4096)
                let incompatible, _ = Test.readFrame worker |> Test.response 2u
                Assert.Equal("invalid_params", incompatible.Value.Code)

                Test.send worker 3u "initialize" (Test.workerInitialize 4096)
                let initialized = Test.requireSuccess 3u worker

                Assert.Equal(
                    2L,
                    initialized
                    |> Test.field "protocolVersion"
                    |> Test.field "major"
                    |> RpcValue.requireInteger "major"
                )

                Assert.Equal(
                    4096L,
                    initialized
                    |> Test.field "limits"
                    |> Test.field "maxFrameBytes"
                    |> RpcValue.requireInteger "maxFrameBytes"
                )

                Test.shutdown worker 4u
            finally
                Test.disposeProcess worker
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``should recover global json selection invalidation through public refresh``() =
        let directory = Test.temporaryDirectory "global-json"

        try
            let version = Test.currentSdkVersion directory
            let globalJson = Path.Combine(directory, "global.json")
            Test.writeGlobalJson directory version
            let project = Test.simpleProject directory "Selected" ".csproj"
            let solution = Test.writeSolution directory [ project ]

            Test.withPipe solution (fun app ->
                let hydratedRevision = Test.hydrateProject app 2u
                Test.writeGlobalJson directory "99.0.100"
                Test.send app 100u "workspace/refresh" RpcValue.emptyMap

                let unavailable, observedRevision =
                    Test.requireSuccessAfterWorkspaceNotifications 100u hydratedRevision app

                Assert.Equal(RpcValue.Boolean true, Test.field "reset" unavailable)

                let unavailableRevision =
                    Test.field "revision" unavailable |> RpcValue.requireInteger "revision"

                Assert.True(unavailableRevision > observedRevision)

                let reset = Test.readMatchingWorkspaceReset unavailableRevision app

                Assert.Contains(
                    Test.values "diagnostics" reset,
                    fun diagnostic ->
                        Test.stringField "code" diagnostic = "workspace.refresh_unverified"
                )

                Test.writeGlobalJson directory version
                Test.send app 101u "workspace/refresh" RpcValue.emptyMap

                let recovered, recoveredObservedRevision =
                    Test.requireSuccessAfterWorkspaceNotifications 101u unavailableRevision app

                Assert.Equal(RpcValue.Boolean false, Test.field "reset" recovered)

                let recoveredRevision =
                    Test.field "revision" recovered |> RpcValue.requireInteger "revision"

                Assert.True(recoveredRevision >= recoveredObservedRevision)

                let freshRevision = Test.hydrateProject app 200u
                Assert.True(freshRevision > recoveredRevision)
                Assert.True(File.Exists globalJson)
                300u)
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``should keep stable failure mappings for missing malformed and incompatible inputs``
        ()
        =
        let directory = Test.temporaryDirectory "failures"

        try
            let malformed = Path.Combine(directory, "Malformed.csproj")
            Test.write malformed "<Project><PropertyGroup>"

            Test.withWorker directory (fun worker ->
                let missing, _ =
                    Test.evaluate worker 2u (Path.Combine(directory, "Missing.csproj"))

                Assert.Equal("msbuild.project_not_found", missing.Value.Code)
                let malformedFailure, _ = Test.evaluate worker 3u malformed
                Assert.Equal("msbuild.project_malformed", malformedFailure.Value.Code)
                4u)

            let incompatibleToolset = Path.Combine(directory, "not-an-sdk")
            Directory.CreateDirectory incompatibleToolset |> ignore
            use incompatible = Test.startWorker incompatibleToolset
            let stdout = incompatible.StandardOutput.ReadToEnd()
            let stderr = incompatible.StandardError.ReadToEnd()
            incompatible.WaitForExit()

            Assert.Equal(70, incompatible.ExitCode)
            Assert.Equal(String.Empty, stdout)
            Assert.Equal("msbuild-host:toolset-load-failed", stderr.Trim())
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``should repair a failed inner dimension in the same worker``() =
        let directory = Test.temporaryDirectory "dimension-recovery"

        try
            let brokenImport = Path.Combine(directory, "Broken.targets")
            Test.write brokenImport "<Project><PropertyGroup>"
            let project = Path.Combine(directory, "Repairable.csproj")

            Test.write
                project
                """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup>
  <Import Project="Broken.targets" Condition="'$(TargetFramework)' == 'net9.0'" />
</Project>
"""

            Test.withWorker directory (fun worker ->
                let failed, _ = Test.evaluate worker 2u project
                Assert.Equal("msbuild.project_malformed", failed.Value.Code)

                Test.write
                    brokenImport
                    ("<Project><PropertyGroup><RepairMarker>repaired</RepairMarker>"
                     + "</PropertyGroup></Project>")

                let repairedError, repaired = Test.evaluate worker 3u project
                Assert.True repairedError.IsNone
                Assert.Equal(3, (Test.values "dimensions" repaired).Length)

                let net9 =
                    Test.values "dimensions" repaired
                    |> Seq.find (fun value ->
                        Test.field "targetFramework" value = RpcValue.String "net9.0")

                Assert.Contains(
                    Test.values "properties" net9,
                    fun value ->
                        Test.stringField "name" value = "RepairMarker"
                        && Test.stringField "value" value = "repaired"
                )

                4u)
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``should complete cancelled export once and reap apphost resources on shutdown``() =
        let directory = Test.temporaryDirectory "cancellation"

        try
            let projects =
                [ for index in 1..20 -> Test.simpleProject directory $"Project{index}" ".fsproj" ]

            let solution = Test.writeSolution directory projects

            Test.withPipe solution (fun app ->
                Test.send app 2u "workspace/export" RpcValue.emptyMap
                let export = Test.requireSuccess 2u app
                let operationId = Test.stringField "operationId" export

                Test.send
                    app
                    3u
                    "operation/cancel"
                    (RpcValue.map [ "operationId", RpcValue.String operationId ])

                let mutable cancelAccepted = false
                let mutable completions = 0

                while completions = 0 do
                    match Test.readFrame app with
                    | Notification("workspace/exportChunk", _) -> ()
                    | Response(3u, error, result) ->
                        Assert.True error.IsNone
                        Assert.Equal(RpcValue.Boolean true, Test.field "accepted" result)
                        cancelAccepted <- true
                    | Notification("operation/completed", parameters) ->
                        Assert.Equal(operationId, Test.stringField "operationId" parameters)
                        Assert.Equal("cancelled", Test.stringField "outcome" parameters)
                        completions <- completions + 1
                    | frame -> failwithf "Unexpected cancellation frame: %A" frame

                Assert.True cancelAccepted
                Assert.Equal(1, completions)
                4u)
        finally
            Directory.Delete(directory, true)
