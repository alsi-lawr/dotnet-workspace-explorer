namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.IO
open Microsoft.VisualStudio.SolutionPersistence.Model
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Workspace scenarios")>]
type WorkspaceExportStreamingTests() =
    [<Fact>]
    member _.``should stream repeatable bounded exports with stable identity cardinality and order``
        ()
        =
        let directory = WorkspaceRpcScenario.temporaryDirectory "pipe-bounded-export-order"

        let projectContents prefix =
            let items =
                [ for index in 1..48 ->
                      $"<Compile Include=\"{prefix}/{String('x', 48)}-{index:D3}.cs\" />" ]
                |> String.concat String.Empty

            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
            + "<TargetFramework>net10.0</TargetFramework>"
            + "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>"
            + $"</PropertyGroup><ItemGroup>{items}</ItemGroup></Project>"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let model = SolutionModel()

            for name in [ "Zulu"; "Alpha"; "Middle" ] do
                model.AddProject($"{name}.csproj", name, null) |> ignore
                File.WriteAllText(Path.Combine(directory, $"{name}.csproj"), projectContents name)

            WorkspaceRpcScenario.save solution model
            use child = WorkspaceRpcScenario.startWorkspaceRpc "solution" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 1u "initialize" WorkspaceRpcScenario.initialize)

                WorkspaceRpcScenario.readFrame child
                |> WorkspaceRpcScenario.response 1u
                |> ignore

                let firstId, firstRevision = WorkspaceRpcScenario.startExport child 2u
                let first = WorkspaceRpcScenario.readExport child firstId firstRevision
                let secondId, secondRevision = WorkspaceRpcScenario.startExport child 3u
                let second = WorkspaceRpcScenario.readExport child secondId secondRevision

                (secondRevision) |> should equal (firstRevision)
                (first.Outcome) |> should equal ("succeeded")
                (second.Outcome) |> should equal ("succeeded")
                (first.ChunkSizes.Length > 1) |> should equal true
                (first.ChunkSizes |> Array.max >= 768) |> should equal true
                (first.LastValues |> Array.filter id) |> should equal ([| true |])
                (first.LastValues[first.LastValues.Length - 1]) |> should equal true
                (second.Nodes.Length) |> should equal (first.Nodes.Length)
                (second.ChunkSizes) |> should equal (first.ChunkSizes)

                let nodeShape node =
                    let capabilities =
                        WorkspaceRpcScenario.field "capabilities" node
                        |> RpcValue.requireArray "capabilities"
                        |> Seq.map (RpcValue.requireString "capability")
                        |> String.concat ","

                    String.concat
                        "\u001f"
                        [ WorkspaceRpcScenario.field "id" node |> RpcValue.requireString "id"
                          WorkspaceRpcScenario.field "kind" node |> RpcValue.requireString "kind"
                          WorkspaceRpcScenario.field "name" node |> RpcValue.requireString "name"
                          WorkspaceRpcScenario.field "loadState" node
                          |> RpcValue.requireString "loadState"
                          capabilities ]

                let firstShapes = first.Nodes |> Array.map nodeShape
                let secondShapes = second.Nodes |> Array.map nodeShape
                (secondShapes) |> should equal (firstShapes)

                let nodeIds =
                    first.Nodes
                    |> Array.map (WorkspaceRpcScenario.field "id" >> RpcValue.requireString "id")

                (nodeIds |> Array.distinct |> Array.length) |> should equal (nodeIds.Length)

                let projectNames =
                    first.Nodes
                    |> Array.filter (fun node ->
                        WorkspaceRpcScenario.field "kind" node = RpcValue.String "project")
                    |> Array.map (
                        WorkspaceRpcScenario.field "name" >> RpcValue.requireString "name"
                    )

                (projectNames) |> should equal ([| "Alpha"; "Middle"; "Zulu" |])
                WorkspaceRpcScenario.shutdown child 4u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``should fail a later export evaluation after non-final chunks exactly once``() =
        let directory =
            WorkspaceRpcScenario.temporaryDirectory "pipe-bounded-export-failure"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let first = Path.Combine(directory, "Alpha.csproj")
            let missing = Path.Combine(directory, "Zulu.csproj")
            let model = SolutionModel()
            model.AddProject("Alpha.csproj", "Alpha", null) |> ignore
            model.AddProject("Zulu.csproj", "Zulu", null) |> ignore
            WorkspaceRpcScenario.writeProject first
            WorkspaceRpcScenario.writeProject missing
            WorkspaceRpcScenario.save solution model
            use child = WorkspaceRpcScenario.startWorkspaceRpc "solution" solution

            try
                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request 1u "initialize" WorkspaceRpcScenario.initialize)

                WorkspaceRpcScenario.readFrame child
                |> WorkspaceRpcScenario.response 1u
                |> ignore

                File.Delete missing

                let operationId, revision = WorkspaceRpcScenario.startExport child 2u
                let exported = WorkspaceRpcScenario.readExport child operationId revision

                (exported.Outcome) |> should equal ("failed")
                (exported.DiagnosticCodes) |> should contain ("not_found")
                (exported.ChunkSizes.Length > 0) |> should equal true
                (exported.LastValues) |> should not' (contain (true))
                (exported.CompletionSequence) |> should equal (int64 exported.ChunkSizes.Length)

                WorkspaceRpcScenario.send
                    child
                    false
                    (WorkspaceRpcScenario.request
                        3u
                        "workspace/operations/cancel"
                        (WorkspaceRpcScenario.map [ "operationId", RpcValue.String operationId ]))

                let cancelError, cancelResult =
                    WorkspaceRpcScenario.readFrame child |> WorkspaceRpcScenario.response 3u

                (cancelError.IsNone) |> should equal true

                (WorkspaceRpcScenario.field "accepted" cancelResult)
                |> should equal (RpcValue.Boolean false)

                WorkspaceRpcScenario.shutdown child 4u
            finally
                WorkspaceRpcScenario.disposeProcess child
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
