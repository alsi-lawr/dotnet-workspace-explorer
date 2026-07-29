namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open System
open System.IO
open System.Threading
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceEditing

module private SolutionEditScenario =
    let temporaryDirectory () =
        let path =
            Path.Combine(
                AppContext.BaseDirectory,
                $".dotnet-workspace-explorer-mutations-{Guid.NewGuid():N}"
            )

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
        match SolutionWorkspaceReader.OpenAsync(path).Result with
        | Success workspace -> workspace
        | Failure failure -> failwithf "Expected workspace, got %A" failure

    let withFullProjectCapabilities (workspace: SolutionWorkspace) =
        let enrichments =
            workspace.Contents.Projects
            |> Seq.map (fun project ->
                { ProjectId = project.Node.Id
                  CapabilityProfile = WorkspaceCapabilityProfile.Full })

        SolutionWorkspaceCapabilities.EnrichProjectCapabilities(workspace, enrichments)

    let writableWorkspace path =
        workspace path |> withFullProjectCapabilities

    let argument id value =
        { ParameterId = CommandParameterId.Create id
          Value = value }

    let request command target arguments =
        { CommandId = CommandId.Create command
          TargetWorkspaceNodeId = target
          Arguments = CommandArguments.Create arguments
          ExpectedRevision = WorkspaceRevision.Create 0L }

    let plan workspace command target arguments =
        SolutionEditor
            .PlanAsync(workspace, request command target arguments, CancellationToken.None)
            .Result

    let actions (plan: PlannedSolutionEdit) =
        seq {
            match plan.FileRename with
            | Some rename ->
                yield WorkspaceEditAction.Rename(rename.Source.Value, rename.Destination.Value)
            | None -> ()

            yield WorkspaceEditAction.ReplaceFile(plan.BackingPath.Value, plan.Contents)
        }

    let apply (workspace: SolutionWorkspace) (plan: PlannedSolutionEdit) =
        let directory = Path.GetDirectoryName workspace.SolutionPath.Value

        let coordinator =
            WorkspaceEditTransaction.CreateProduction(
                WorkspaceArtifactPath.Create directory,
                fun () -> workspace.Descriptor.Revision
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
        workspace.Contents.Projects |> Seq.find (fun value -> value.Node.Name = name)

    let folder (workspace: SolutionWorkspace) (path: string) =
        workspace.Contents.Folders |> Seq.find (fun value -> value.Path = path)

    let item (workspace: SolutionWorkspace) (name: string) =
        workspace.Contents.Items |> Seq.find (fun value -> value.Node.Name = name)

    let configuration (workspace: SolutionWorkspace) (name: string) =
        workspace.Contents.BuildTypes |> Seq.find (fun value -> value.Name = name)

    let platform (workspace: SolutionWorkspace) (name: string) =
        workspace.Contents.Platforms |> Seq.find (fun value -> value.Name = name)

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

        [ "solution.folder.add", Some empty.Node.Id, [ argument "name" (Text "new") ]
          "solution.folder.import-directory",
          None,
          [ argument "path" (Path(WorkspaceArtifactPath.Create(Path.Combine(directory, "assets")))) ]
          "solution.folder.remove", Some empty.Node.Id, []
          "solution.item.add",
          Some source.Node.Id,
          [ argument
                "path"
                (Path(WorkspaceArtifactPath.Create(Path.Combine(directory, "new-item.txt")))) ]
          "solution.item.remove", Some existingItem.Node.Id, []
          "solution.project.add",
          None,
          [ argument
                "path"
                (Path(WorkspaceArtifactPath.Create(Path.Combine(directory, "New.csproj")))) ]
          "solution.project.remove", Some one.Node.Id, []
          "solution.project.rename", Some one.Node.Id, [ argument "name" (Text "Renamed") ]
          "solution.project.move", Some one.Node.Id, [ argument "folder" (Node source.Node.Id) ]
          "solution.project.update-path",
          Some one.Node.Id,
          [ argument
                "path"
                (Path(WorkspaceArtifactPath.Create(Path.Combine(directory, "Moved.csproj")))) ]
          "solution.build-type.add", None, [ argument "name" (Text "Profile") ]
          "solution.build-type.remove", Some config.Id, []
          "solution.platform.add", None, [ argument "name" (Text "arm64") ]
          "solution.platform.remove", Some platformValue.Id, []
          "solution.project-configuration.set",
          Some one.Node.Id,
          [ argument "solutionBuildType" (Text "Debug")
            argument "solutionPlatform" (Text "Any CPU")
            argument "projectBuildType" (Text "ProjectDebug")
            argument "projectPlatform" (Text "ProjectPlatform")
            argument "builds" (Boolean false)
            argument "deploys" (Boolean true) ]
          "solution.project-configuration.remove",
          Some one.Node.Id,
          [ argument "solutionBuildType" (Text "Debug")
            argument "solutionPlatform" (Text "Any CPU") ]
          "solution.dependency.add", Some one.Node.Id, [ argument "dependency" (Node two.Node.Id) ]
          "solution.dependency.remove",
          Some one.Node.Id,
          [ argument "dependency" (Node two.Node.Id) ] ]
