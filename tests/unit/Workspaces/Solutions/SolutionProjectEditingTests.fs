namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open System.IO
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open FsUnit.Xunit
open Xunit

[<Collection("Solution edits")>]
type SolutionProjectEditingTests() =
    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``should hide and refuse project writes until a managed project is hydrated``
        (extension: string)
        =
        let directory = SolutionEditScenario.temporaryDirectory ()

        try
            let solution, _ = SolutionEditScenario.preparedWorkspace directory extension false
            let workspace = SolutionEditScenario.workspace solution
            let project = SolutionEditScenario.project workspace "One"

            SolutionEditor.Discover(workspace, Some project.Node.Id) |> should be Empty

            match
                SolutionEditScenario.plan
                    workspace
                    "solution.project.rename"
                    (Some project.Node.Id)
                    [ SolutionEditScenario.argument "name" (Text "Renamed") ]
            with
            | Failure(UnsupportedCapability(capability, _)) ->
                capability |> should equal WorkspaceCapabilityId.Write
            | outcome -> failwithf "Expected an unsupported capability refusal, got %A" outcome
        finally
            SolutionEditScenario.delete directory

    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``should keep external projects and logical removes within solution metadata for both formats``
        (extension: string)
        =
        let directory = SolutionEditScenario.temporaryDirectory ()
        let external = SolutionEditScenario.temporaryDirectory ()

        try
            let solution, workspace =
                SolutionEditScenario.preparedWorkspace directory extension false

            let one = SolutionEditScenario.project workspace "One"
            let source = SolutionEditScenario.folder workspace "/src/"
            let before = File.ReadAllBytes solution
            let externalProject = Path.Combine(external, "External.csproj")
            let externalItem = Path.Combine(external, "external.txt")
            File.WriteAllText(externalProject, "<Project />")
            File.WriteAllText(externalItem, "external")

            match
                SolutionEditScenario.plan
                    workspace
                    "solution.project.add"
                    None
                    [ SolutionEditScenario.argument
                          "path"
                          (Path(WorkspaceArtifactPath.Create externalProject)) ]
            with
            | Success plan ->
                plan.Request.Intents.Contains WorkspaceEditIntent.AccessExternalPath
                |> should equal true

                File.ReadAllBytes solution |> should equal before
                SolutionEditScenario.apply workspace plan

                let withExternal = SolutionEditScenario.writableWorkspace solution

                let externalProjection =
                    withExternal.Contents.Projects
                    |> Seq.find (fun value -> value.Path.AbsolutePath.Value = externalProject)

                match
                    SolutionEditScenario.plan
                        withExternal
                        "solution.project.rename"
                        (Some externalProjection.Node.Id)
                        [ SolutionEditScenario.argument "name" (Text "RenamedExternal") ]
                with
                | Success renamePlan ->
                    renamePlan.Request.Intents.Contains WorkspaceEditIntent.AccessExternalPath
                    |> should equal true

                    renamePlan.Request.Targets |> Seq.map _.Value |> should contain externalProject

                    renamePlan.Request.Targets
                    |> Seq.map _.Value
                    |> should contain (Path.Combine(external, "RenamedExternal.csproj"))
                | Failure failure -> failwithf "External project rename plan failed: %A" failure
            | Failure failure -> failwithf "External project plan failed: %A" failure

            match
                SolutionEditScenario.plan
                    workspace
                    "solution.item.add"
                    (Some source.Node.Id)
                    [ SolutionEditScenario.argument
                          "path"
                          (Path(WorkspaceArtifactPath.Create externalItem)) ]
            with
            | Success plan ->
                plan.Request.Intents.Contains WorkspaceEditIntent.AccessExternalPath
                |> should equal true

                plan.Request.Targets
                |> Seq.exists (fun value -> value.Value = externalItem)
                |> should equal true
            | Failure failure -> failwithf "External solution item plan failed: %A" failure

            match
                SolutionEditScenario.plan workspace "solution.project.remove" (Some one.Node.Id) []
            with
            | Success _ -> File.Exists(Path.Combine(directory, "One.csproj")) |> should equal true
            | Failure failure -> failwithf "Metadata-only removal plan failed: %A" failure
        finally
            SolutionEditScenario.delete directory
            SolutionEditScenario.delete external
