namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System
open System.IO
open System.Threading
open Dotnet.CLI.Plus
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution
open FsUnit.Xunit
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Xunit

module private SolutionMutation =
    let temporaryDirectory () =
        let path =
            Path.Combine(AppContext.BaseDirectory, $".dotnet-cli-plus-mutations-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let delete path =
        if Directory.Exists path then
            Directory.Delete(path, true)

    let save path (model: SolutionModel) =
        SolutionSerializers
            .GetSerializerByMoniker(path)
            .SaveAsync(path, model, CancellationToken.None)
            .GetAwaiter()
            .GetResult()

    let workspace path =
        match SolutionStore.OpenAsync(path).Result with
        | Success workspace -> workspace
        | Failure failure -> failwithf "Expected workspace, got %A" failure

    let withFullProjectCapabilities (workspace: SolutionWorkspace) =
        let enrichments =
            workspace.RootProjection.Projects
            |> Seq.map (fun project ->
                { ProjectId = project.Node.NodeId
                  CapabilityProfile = WorkspaceCapabilityProfile.Full })

        SolutionProjection.EnrichProjectCapabilities(workspace, enrichments)

    let writableWorkspace path =
        workspace path |> withFullProjectCapabilities

    let argument id value =
        { ParameterId = CommandParameterId.Create id
          Value = value }

    let request command target arguments =
        { CommandId = CommandId.Create command
          TargetId = target
          Arguments = CommandArguments.Create arguments
          ExpectedRevision = WorkspaceRevision.Create 0L }

    let plan workspace command target arguments =
        SolutionPersistenceMutator
            .PlanAsync(workspace, request command target arguments, CancellationToken.None)
            .Result

    let actions (plan: SolutionMutationPlan) =
        seq {
            match plan.FileRename with
            | Some rename ->
                yield MutationAction.Rename(rename.Source.Value, rename.Destination.Value)
            | None -> ()

            yield MutationAction.ReplaceFile(plan.BackingPath.Value, plan.Contents)
        }

    let apply (workspace: SolutionWorkspace) plan =
        let directory = Path.GetDirectoryName workspace.BackingPath.Value

        let coordinator =
            MutationCoordinator.CreateProduction(
                WorkspaceArtifactPath.Create directory,
                fun () -> workspace.WorkspaceDescriptor.WorkspaceRevision
            )

        match coordinator.Prepare(plan.Request, actions plan) with
        | Failure failure -> failwithf "Preview failed: %A" failure
        | Success preview ->
            match
                coordinator.Execute(
                    plan.Request,
                    actions plan,
                    preview.Confirmation,
                    CancellationToken.None
                )
            with
            | Success Applied -> ()
            | Success(RolledBack failure)
            | Failure failure -> failwithf "Apply failed: %A" failure

    let project (workspace: SolutionWorkspace) (name: string) =
        workspace.RootProjection.Projects
        |> Seq.find (fun value -> value.Node.Name = name)

    let folder (workspace: SolutionWorkspace) (path: string) =
        workspace.RootProjection.Folders |> Seq.find (fun value -> value.Path = path)

    let item (workspace: SolutionWorkspace) (name: string) =
        workspace.RootProjection.Items |> Seq.find (fun value -> value.Node.Name = name)

    let configuration (workspace: SolutionWorkspace) (name: string) =
        workspace.RootProjection.BuildTypes |> Seq.find (fun value -> value.Name = name)

    let platform (workspace: SolutionWorkspace) (name: string) =
        workspace.RootProjection.Platforms |> Seq.find (fun value -> value.Name = name)

    let preparedWorkspace directory extension dependency =
        let solution = Path.Combine(directory, $"Demo{extension}")
        Directory.CreateDirectory(Path.Combine(directory, "assets")) |> ignore
        File.WriteAllText(Path.Combine(directory, "src-item.txt"), "item")
        File.WriteAllText(Path.Combine(directory, "new-item.txt"), "new item")
        File.WriteAllText(Path.Combine(directory, "One.csproj"), "<Project />")
        File.WriteAllText(Path.Combine(directory, "Two.csproj"), "<Project />")
        File.WriteAllText(Path.Combine(directory, "New.csproj"), "<Project />")
        File.WriteAllText(Path.Combine(directory, "Moved.csproj"), "<Project />")
        let model = SolutionModel()
        let source = model.AddFolder "/src/"
        source.AddFile "src-item.txt"
        model.AddFolder "/empty/" |> ignore
        let one = model.AddProject("One.csproj", null, null)
        let two = model.AddProject("Two.csproj", null, null)
        model.AddBuildType "Debug"
        model.AddBuildType "Release"
        model.AddPlatform "Any CPU"
        model.AddPlatform "x64"

        if dependency then
            one.AddDependency two

        one.AddProjectConfigurationRule(
            ConfigurationRule(BuildDimension.BuildType, "Debug", "Any CPU", "Debug")
        )

        save solution model
        solution, writableWorkspace solution

    let cases (directory: string) (workspace: SolutionWorkspace) =
        let source = folder workspace "/src/"
        let empty = folder workspace "/empty/"
        let one = project workspace "One"
        let two = project workspace "Two"
        let existingItem = item workspace "src-item.txt"
        let config = configuration workspace "Debug"
        let platformValue = platform workspace "Any CPU"

        [ "solution.folder.add", Some empty.Node.NodeId, [ argument "name" (Text "new") ]
          "solution.folder.import-directory",
          None,
          [ argument "path" (Path(WorkspaceArtifactPath.Create(Path.Combine(directory, "assets")))) ]
          "solution.folder.remove", Some empty.Node.NodeId, []
          "solution.item.add",
          Some source.Node.NodeId,
          [ argument
                "path"
                (Path(WorkspaceArtifactPath.Create(Path.Combine(directory, "new-item.txt")))) ]
          "solution.item.remove", Some existingItem.Node.NodeId, []
          "solution.project.add",
          None,
          [ argument
                "path"
                (Path(WorkspaceArtifactPath.Create(Path.Combine(directory, "New.csproj")))) ]
          "solution.project.remove", Some one.Node.NodeId, []
          "solution.project.rename", Some one.Node.NodeId, [ argument "name" (Text "Renamed") ]
          "solution.project.move",
          Some one.Node.NodeId,
          [ argument "folder" (Node source.Node.NodeId) ]
          "solution.project.update-path",
          Some one.Node.NodeId,
          [ argument
                "path"
                (Path(WorkspaceArtifactPath.Create(Path.Combine(directory, "Moved.csproj")))) ]
          "solution.build-type.add", None, [ argument "name" (Text "Profile") ]
          "solution.build-type.remove", Some config.NodeId, []
          "solution.platform.add", None, [ argument "name" (Text "arm64") ]
          "solution.platform.remove", Some platformValue.NodeId, []
          "solution.project-configuration.set",
          Some one.Node.NodeId,
          [ argument "solutionBuildType" (Text "Debug")
            argument "solutionPlatform" (Text "Any CPU")
            argument "projectBuildType" (Text "ProjectDebug")
            argument "projectPlatform" (Text "ProjectPlatform")
            argument "builds" (Boolean false)
            argument "deploys" (Boolean true) ]
          "solution.project-configuration.remove",
          Some one.Node.NodeId,
          [ argument "solutionBuildType" (Text "Debug")
            argument "solutionPlatform" (Text "Any CPU") ]
          "solution.dependency.add",
          Some one.Node.NodeId,
          [ argument "dependency" (Node two.Node.NodeId) ]
          "solution.dependency.remove",
          Some one.Node.NodeId,
          [ argument "dependency" (Node two.Node.NodeId) ] ]

type SolutionMutationTests() =
    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``should plan every solution mutation command for both supported formats``
        (extension: string)
        =
        let directory = SolutionMutation.temporaryDirectory ()

        try
            let mutable executions = 0
            let _, initial = SolutionMutation.preparedWorkspace directory extension false
            let commands = SolutionMutation.cases directory initial

            for command, target, arguments in commands do
                let solution, initialWorkspace =
                    SolutionMutation.preparedWorkspace
                        directory
                        extension
                        (command = "solution.dependency.remove")

                let workspace =
                    if command = "solution.project-configuration.remove" then
                        let _, setTarget, setArguments =
                            commands
                            |> List.find (fun (id, _, _) ->
                                id = "solution.project-configuration.set")

                        match
                            SolutionMutation.plan
                                initialWorkspace
                                "solution.project-configuration.set"
                                setTarget
                                setArguments
                        with
                        | Success setPlan ->
                            SolutionMutation.apply initialWorkspace setPlan
                            SolutionMutation.writableWorkspace solution
                        | Failure failure ->
                            failwithf "Configuration setup was not planned: %A" failure
                    else
                        initialWorkspace

                match SolutionMutation.plan workspace command target arguments with
                | Success plan ->
                    SolutionMutation.apply workspace plan
                    let reopened = SolutionMutation.workspace solution

                    match command with
                    | "solution.folder.add" ->
                        reopened.RootProjection.Folders
                        |> Seq.exists (fun value -> value.Path = "/empty/new/")
                        |> should equal true
                    | "solution.folder.import-directory" ->
                        reopened.RootProjection.Folders
                        |> Seq.exists (fun value -> value.Path = "/assets/")
                        |> should equal true
                    | "solution.folder.remove" ->
                        reopened.RootProjection.Folders
                        |> Seq.exists (fun value -> value.Path = "/empty/")
                        |> should equal false
                    | "solution.item.add" ->
                        reopened.RootProjection.Items
                        |> Seq.exists (fun value -> value.Node.Name = "new-item.txt")
                        |> should equal true
                    | "solution.item.remove" ->
                        reopened.RootProjection.Items
                        |> Seq.exists (fun value -> value.Node.Name = "src-item.txt")
                        |> should equal false

                        File.Exists(Path.Combine(directory, "src-item.txt")) |> should equal true
                    | "solution.project.add" ->
                        reopened.RootProjection.Projects
                        |> Seq.exists (fun value -> value.Node.Name = "New")
                        |> should equal true
                    | "solution.project.remove" ->
                        reopened.RootProjection.Projects
                        |> Seq.exists (fun value -> value.Node.Name = "One")
                        |> should equal false

                        File.Exists(Path.Combine(directory, "One.csproj")) |> should equal true
                    | "solution.project.rename" ->
                        let renamed = SolutionMutation.project reopened "Renamed"
                        renamed.Path.SolutionRelativePath |> should equal "Renamed.csproj"
                        File.Exists(Path.Combine(directory, "Renamed.csproj")) |> should equal true
                        File.Exists(Path.Combine(directory, "One.csproj")) |> should equal false
                    | "solution.project.move" ->
                        (SolutionMutation.project reopened "One").ParentFolderPath
                        |> should equal (Some "/src/")
                    | "solution.project.update-path" ->
                        (SolutionMutation.project reopened "Moved").Path.SolutionRelativePath
                        |> should equal "Moved.csproj"

                        File.Exists(Path.Combine(directory, "One.csproj")) |> should equal true
                        File.Exists(Path.Combine(directory, "Moved.csproj")) |> should equal true
                    | "solution.build-type.add" ->
                        reopened.RootProjection.BuildTypes
                        |> Seq.exists (fun value -> value.Name = "Profile")
                        |> should equal true
                    | "solution.build-type.remove" ->
                        reopened.RootProjection.BuildTypes
                        |> Seq.exists (fun value -> value.Name = "Debug")
                        |> should equal false
                    | "solution.platform.add" ->
                        reopened.RootProjection.Platforms
                        |> Seq.exists (fun value -> value.Name = "arm64")
                        |> should equal true
                    | "solution.platform.remove" ->
                        reopened.RootProjection.Platforms
                        |> Seq.exists (fun value -> value.Name = "Any CPU")
                        |> should equal false
                    | "solution.project-configuration.set" ->
                        let mapping =
                            (SolutionMutation.project reopened "One").ConfigurationMappings
                            |> Seq.find (fun value ->
                                value.SolutionBuildType = "Debug"
                                && value.SolutionPlatform = "Any CPU")

                        mapping.ProjectBuildType |> should equal "ProjectDebug"
                        mapping.ProjectPlatform |> should equal "ProjectPlatform"
                        mapping.Builds |> should equal false
                        mapping.Deploys |> should equal true
                    | "solution.project-configuration.remove" ->
                        let mapping =
                            (SolutionMutation.project reopened "One").ConfigurationMappings
                            |> Seq.find (fun value ->
                                value.SolutionBuildType = "Debug"
                                && value.SolutionPlatform = "Any CPU")

                        mapping.ProjectBuildType = "ProjectDebug" |> should equal false
                        mapping.ProjectPlatform = "ProjectPlatform" |> should equal false
                    | "solution.dependency.add" ->
                        reopened.RootProjection.Dependencies.Length |> should equal 1
                    | "solution.dependency.remove" ->
                        reopened.RootProjection.Dependencies.Length |> should equal 0
                    | value -> failwithf "Unasserted command %s" value

                    executions <- executions + 1
                | Failure failure -> failwithf "%s was not planned: %A" command failure

            executions |> should equal 18
        finally
            SolutionMutation.delete directory

    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``should reject duplicate missing and case-only solution mutations deterministically for both formats``
        (extension: string)
        =
        let directory = SolutionMutation.temporaryDirectory ()
        let foreignDirectory = SolutionMutation.temporaryDirectory ()

        try
            let _, workspace = SolutionMutation.preparedWorkspace directory extension false
            let one = SolutionMutation.project workspace "One"
            let two = SolutionMutation.project workspace "Two"
            let source = SolutionMutation.folder workspace "/src/"
            let blockedDestination = Path.Combine(directory, "Blocked.csproj")
            Directory.CreateDirectory blockedDestination |> ignore

            let _, foreignWorkspace =
                SolutionMutation.preparedWorkspace foreignDirectory extension false

            let foreignFolder = SolutionMutation.folder foreignWorkspace "/src/"

            let cases =
                [ SolutionMutation.plan
                      workspace
                      "solution.folder.add"
                      None
                      [ SolutionMutation.argument "name" (Text "src") ]
                  SolutionMutation.plan workspace "solution.project.remove" None []
                  SolutionMutation.plan
                      workspace
                      "solution.project.add"
                      None
                      [ SolutionMutation.argument
                            "path"
                            (Path(
                                WorkspaceArtifactPath.Create(
                                    Path.Combine(directory, "Missing.csproj")
                                )
                            )) ]
                  SolutionMutation.plan
                      workspace
                      "solution.project.update-path"
                      (Some one.Node.NodeId)
                      [ SolutionMutation.argument
                            "path"
                            (Path(
                                WorkspaceArtifactPath.Create(
                                    Path.Combine(directory, "Missing.csproj")
                                )
                            )) ]
                  SolutionMutation.plan
                      workspace
                      "solution.project.update-path"
                      (Some one.Node.NodeId)
                      [ SolutionMutation.argument
                            "path"
                            (Path(
                                WorkspaceArtifactPath.Create(Path.Combine(directory, "Two.csproj"))
                            )) ]
                  SolutionMutation.plan
                      workspace
                      "solution.project.rename"
                      (Some one.Node.NodeId)
                      [ SolutionMutation.argument "name" (Text "Two") ]
                  SolutionMutation.plan
                      workspace
                      "solution.project.rename"
                      (Some one.Node.NodeId)
                      [ SolutionMutation.argument "name" (Text "Blocked") ]
                  SolutionMutation.plan
                      workspace
                      "solution.project.rename"
                      (Some one.Node.NodeId)
                      [ SolutionMutation.argument "name" (Text "nested/name") ]
                  SolutionMutation.plan
                      workspace
                      "solution.project-configuration.set"
                      (Some one.Node.NodeId)
                      [ SolutionMutation.argument "solutionBuildType" (Text "Debug")
                        SolutionMutation.argument "solutionPlatform" (Text "Any CPU")
                        SolutionMutation.argument "projectBuildType" (Text "Debug")
                        SolutionMutation.argument "projectPlatform" (Text "Any CPU")
                        SolutionMutation.argument "deploys" (Boolean false) ]
                  SolutionMutation.plan
                      workspace
                      "solution.project-configuration.remove"
                      (Some two.Node.NodeId)
                      [ SolutionMutation.argument "solutionBuildType" (Text "Missing")
                        SolutionMutation.argument "solutionPlatform" (Text "Any CPU") ]
                  SolutionMutation.plan
                      workspace
                      "solution.folder.add"
                      (Some foreignFolder.Node.NodeId)
                      [ SolutionMutation.argument "name" (Text "unknown") ] ]

            cases
            |> List.iter (function
                | Failure(InvalidInput _) -> ()
                | Failure(NotFound _) -> ()
                | outcome -> failwithf "Expected deterministic refusal, got %A" outcome)

            Directory.Exists blockedDestination |> should equal true

            match
                SolutionMutation.plan
                    workspace
                    "solution.project.rename"
                    (Some one.Node.NodeId)
                    [ SolutionMutation.argument "name" (Text "one") ]
            with
            | Success plan -> plan.FileRename.IsSome |> should equal true
            | Failure failure -> failwithf "Case-only rename was refused: %A" failure

            match
                SolutionMutation.plan
                    workspace
                    "solution.folder.remove"
                    (Some source.Node.NodeId)
                    [ SolutionMutation.argument "recursive" (Boolean true) ]
            with
            | Success plan ->
                plan.Request.Intents.Contains MutationIntent.RecursiveDelete
                |> should equal true
            | Failure failure -> failwithf "Recursive metadata removal was refused: %A" failure
        finally
            SolutionMutation.delete directory
            SolutionMutation.delete foreignDirectory

    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``should hide and refuse project writes until a managed project is hydrated``
        (extension: string)
        =
        let directory = SolutionMutation.temporaryDirectory ()

        try
            let solution, _ = SolutionMutation.preparedWorkspace directory extension false
            let workspace = SolutionMutation.workspace solution
            let project = SolutionMutation.project workspace "One"

            SolutionPersistenceMutator.Discover(workspace, Some project.Node.NodeId)
            |> should be Empty

            match
                SolutionMutation.plan
                    workspace
                    "solution.project.rename"
                    (Some project.Node.NodeId)
                    [ SolutionMutation.argument "name" (Text "Renamed") ]
            with
            | Failure(UnsupportedCapability(capability, _)) ->
                capability |> should equal WorkspaceCapabilityId.Write
            | outcome -> failwithf "Expected an unsupported capability refusal, got %A" outcome
        finally
            SolutionMutation.delete directory

    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``should keep external projects and logical removes within solution metadata for both formats``
        (extension: string)
        =
        let directory = SolutionMutation.temporaryDirectory ()
        let external = SolutionMutation.temporaryDirectory ()

        try
            let solution, workspace =
                SolutionMutation.preparedWorkspace directory extension false

            let one = SolutionMutation.project workspace "One"
            let source = SolutionMutation.folder workspace "/src/"
            let before = File.ReadAllBytes solution
            let externalProject = Path.Combine(external, "External.csproj")
            let externalItem = Path.Combine(external, "external.txt")
            File.WriteAllText(externalProject, "<Project />")
            File.WriteAllText(externalItem, "external")

            match
                SolutionMutation.plan
                    workspace
                    "solution.project.add"
                    None
                    [ SolutionMutation.argument
                          "path"
                          (Path(WorkspaceArtifactPath.Create externalProject)) ]
            with
            | Success plan ->
                plan.Request.Intents.Contains MutationIntent.AccessExternalPath
                |> should equal true

                File.ReadAllBytes solution |> should equal before
                SolutionMutation.apply workspace plan

                let withExternal = SolutionMutation.writableWorkspace solution

                let externalProjection =
                    withExternal.RootProjection.Projects
                    |> Seq.find (fun value -> value.Path.AbsolutePath.Value = externalProject)

                match
                    SolutionMutation.plan
                        withExternal
                        "solution.project.rename"
                        (Some externalProjection.Node.NodeId)
                        [ SolutionMutation.argument "name" (Text "RenamedExternal") ]
                with
                | Success renamePlan ->
                    renamePlan.Request.Intents.Contains MutationIntent.AccessExternalPath
                    |> should equal true

                    renamePlan.Request.Targets |> Seq.map _.Value |> should contain externalProject

                    renamePlan.Request.Targets
                    |> Seq.map _.Value
                    |> should contain (Path.Combine(external, "RenamedExternal.csproj"))
                | Failure failure -> failwithf "External project rename plan failed: %A" failure
            | Failure failure -> failwithf "External project plan failed: %A" failure

            match
                SolutionMutation.plan
                    workspace
                    "solution.item.add"
                    (Some source.Node.NodeId)
                    [ SolutionMutation.argument
                          "path"
                          (Path(WorkspaceArtifactPath.Create externalItem)) ]
            with
            | Success plan ->
                plan.Request.Intents.Contains MutationIntent.AccessExternalPath
                |> should equal true

                plan.Request.Targets
                |> Seq.exists (fun value -> value.Value = externalItem)
                |> should equal true
            | Failure failure -> failwithf "External solution item plan failed: %A" failure

            match
                SolutionMutation.plan workspace "solution.project.remove" (Some one.Node.NodeId) []
            with
            | Success _ -> File.Exists(Path.Combine(directory, "One.csproj")) |> should equal true
            | Failure failure -> failwithf "Metadata-only removal plan failed: %A" failure
        finally
            SolutionMutation.delete directory
            SolutionMutation.delete external
