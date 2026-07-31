namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open System.IO
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open FsUnit.Xunit
open Xunit

[<Collection("Solution edits")>]
type SolutionEditPlanningTests() =
    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``supported .sln and .slnx formats plan and apply every solution mutation command``
        (extension: string)
        =
        let directory = SolutionEditScenario.temporaryDirectory ()

        try
            let mutable executions = 0
            let _, initial = SolutionEditScenario.preparedWorkspace directory extension false
            let commands = SolutionEditScenario.cases directory initial

            for command, target, arguments in commands do
                let solution, initialWorkspace =
                    SolutionEditScenario.preparedWorkspace
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
                            SolutionEditScenario.plan
                                initialWorkspace
                                "solution.project-configuration.set"
                                setTarget
                                setArguments
                        with
                        | Success setPlan ->
                            SolutionEditScenario.apply initialWorkspace setPlan
                            SolutionEditScenario.writableWorkspace solution
                        | Failure failure ->
                            failwithf "Configuration setup was not planned: %A" failure
                    else
                        initialWorkspace

                match SolutionEditScenario.plan workspace command target arguments with
                | Success plan ->
                    SolutionEditScenario.apply workspace plan
                    let reopened = SolutionEditScenario.workspace solution

                    match command with
                    | "solution.folder.add" ->
                        reopened.Contents.Folders
                        |> Seq.exists (fun value -> value.Path = "/empty/new/")
                        |> should equal true
                    | "solution.folder.import-directory" ->
                        reopened.Contents.Folders
                        |> Seq.exists (fun value -> value.Path = "/assets/")
                        |> should equal true
                    | "solution.folder.remove" ->
                        reopened.Contents.Folders
                        |> Seq.exists (fun value -> value.Path = "/empty/")
                        |> should equal false
                    | "solution.item.add" ->
                        reopened.Contents.Items
                        |> Seq.exists (fun value -> value.Node.Name = "new-item.txt")
                        |> should equal true
                    | "solution.item.remove" ->
                        reopened.Contents.Items
                        |> Seq.exists (fun value -> value.Node.Name = "src-item.txt")
                        |> should equal false

                        File.Exists(Path.Combine(directory, "src-item.txt")) |> should equal true
                    | "solution.project.add" ->
                        reopened.Contents.Projects
                        |> Seq.exists (fun value -> value.Node.Name = "New")
                        |> should equal true
                    | "solution.project.remove" ->
                        reopened.Contents.Projects
                        |> Seq.exists (fun value -> value.Node.Name = "One")
                        |> should equal false

                        File.Exists(Path.Combine(directory, "One.csproj")) |> should equal true
                    | "solution.project.rename" ->
                        let renamed = SolutionEditScenario.project reopened "Renamed"
                        renamed.Path.SolutionRelativePath |> should equal "Renamed.csproj"
                        File.Exists(Path.Combine(directory, "Renamed.csproj")) |> should equal true
                        File.Exists(Path.Combine(directory, "One.csproj")) |> should equal false
                    | "solution.project.move" ->
                        (SolutionEditScenario.project reopened "One").ParentFolderPath
                        |> should equal (Some "/src/")
                    | "solution.project.update-path" ->
                        (SolutionEditScenario.project reopened "Moved").Path.SolutionRelativePath
                        |> should equal "Moved.csproj"

                        File.Exists(Path.Combine(directory, "One.csproj")) |> should equal true
                        File.Exists(Path.Combine(directory, "Moved.csproj")) |> should equal true
                    | "solution.build-type.add" ->
                        reopened.Contents.BuildTypes
                        |> Seq.exists (fun value -> value.Name = "Profile")
                        |> should equal true
                    | "solution.build-type.remove" ->
                        reopened.Contents.BuildTypes
                        |> Seq.exists (fun value -> value.Name = "Debug")
                        |> should equal false
                    | "solution.platform.add" ->
                        reopened.Contents.Platforms
                        |> Seq.exists (fun value -> value.Name = "arm64")
                        |> should equal true
                    | "solution.platform.remove" ->
                        reopened.Contents.Platforms
                        |> Seq.exists (fun value -> value.Name = "Any CPU")
                        |> should equal false
                    | "solution.project-configuration.set" ->
                        let mapping =
                            (SolutionEditScenario.project reopened "One").ConfigurationMappings
                            |> Seq.find (fun value ->
                                value.SolutionBuildType = "Debug"
                                && value.SolutionPlatform = "Any CPU")

                        mapping.ProjectBuildType |> should equal "ProjectDebug"
                        mapping.ProjectPlatform |> should equal "ProjectPlatform"
                        mapping.Builds |> should equal false
                        mapping.Deploys |> should equal true
                    | "solution.project-configuration.remove" ->
                        let mapping =
                            (SolutionEditScenario.project reopened "One").ConfigurationMappings
                            |> Seq.find (fun value ->
                                value.SolutionBuildType = "Debug"
                                && value.SolutionPlatform = "Any CPU")

                        mapping.ProjectBuildType = "ProjectDebug" |> should equal false
                        mapping.ProjectPlatform = "ProjectPlatform" |> should equal false
                    | "solution.dependency.add" ->
                        reopened.Contents.Dependencies.Length |> should equal 1
                    | "solution.dependency.remove" ->
                        reopened.Contents.Dependencies.Length |> should equal 0
                    | value -> failwithf "Unasserted command %s" value

                    executions <- executions + 1
                | Failure failure -> failwithf "%s was not planned: %A" command failure

            executions |> should equal 18
        finally
            SolutionEditScenario.delete directory

    [<Theory>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``supported .sln and .slnx formats deterministically reject duplicate, missing, and invalid mutations``
        (extension: string)
        =
        let directory = SolutionEditScenario.temporaryDirectory ()
        let foreignDirectory = SolutionEditScenario.temporaryDirectory ()

        try
            let _, workspace = SolutionEditScenario.preparedWorkspace directory extension false
            let one = SolutionEditScenario.project workspace "One"
            let two = SolutionEditScenario.project workspace "Two"
            let source = SolutionEditScenario.folder workspace "/src/"
            let blockedDestination = Path.Combine(directory, "Blocked.csproj")
            Directory.CreateDirectory blockedDestination |> ignore

            let _, foreignWorkspace =
                SolutionEditScenario.preparedWorkspace foreignDirectory extension false

            let foreignFolder = SolutionEditScenario.folder foreignWorkspace "/src/"

            let cases =
                [ SolutionEditScenario.plan
                      workspace
                      "solution.folder.add"
                      None
                      [ SolutionEditScenario.argument "name" (Text "src") ]
                  SolutionEditScenario.plan workspace "solution.project.remove" None []
                  SolutionEditScenario.plan
                      workspace
                      "solution.project.add"
                      None
                      [ SolutionEditScenario.argument
                            "path"
                            (Path(
                                WorkspaceArtifactPath.Create(
                                    Path.Combine(directory, "Missing.csproj")
                                )
                            )) ]
                  SolutionEditScenario.plan
                      workspace
                      "solution.project.update-path"
                      (Some one.Node.Id)
                      [ SolutionEditScenario.argument
                            "path"
                            (Path(
                                WorkspaceArtifactPath.Create(
                                    Path.Combine(directory, "Missing.csproj")
                                )
                            )) ]
                  SolutionEditScenario.plan
                      workspace
                      "solution.project.update-path"
                      (Some one.Node.Id)
                      [ SolutionEditScenario.argument
                            "path"
                            (Path(
                                WorkspaceArtifactPath.Create(Path.Combine(directory, "Two.csproj"))
                            )) ]
                  SolutionEditScenario.plan
                      workspace
                      "solution.project.rename"
                      (Some one.Node.Id)
                      [ SolutionEditScenario.argument "name" (Text "Two") ]
                  SolutionEditScenario.plan
                      workspace
                      "solution.project.rename"
                      (Some one.Node.Id)
                      [ SolutionEditScenario.argument "name" (Text "Blocked") ]
                  SolutionEditScenario.plan
                      workspace
                      "solution.project.rename"
                      (Some one.Node.Id)
                      [ SolutionEditScenario.argument "name" (Text "nested/name") ]
                  SolutionEditScenario.plan
                      workspace
                      "solution.project-configuration.set"
                      (Some one.Node.Id)
                      [ SolutionEditScenario.argument "solutionBuildType" (Text "Debug")
                        SolutionEditScenario.argument "solutionPlatform" (Text "Any CPU")
                        SolutionEditScenario.argument "projectBuildType" (Text "Debug")
                        SolutionEditScenario.argument "projectPlatform" (Text "Any CPU")
                        SolutionEditScenario.argument "deploys" (Boolean false) ]
                  SolutionEditScenario.plan
                      workspace
                      "solution.project-configuration.remove"
                      (Some two.Node.Id)
                      [ SolutionEditScenario.argument "solutionBuildType" (Text "Missing")
                        SolutionEditScenario.argument "solutionPlatform" (Text "Any CPU") ]
                  SolutionEditScenario.plan
                      workspace
                      "solution.folder.add"
                      (Some foreignFolder.Node.Id)
                      [ SolutionEditScenario.argument "name" (Text "unknown") ] ]

            cases
            |> List.iter (function
                | Failure(InvalidInput _) -> ()
                | Failure(NotFound _) -> ()
                | outcome -> failwithf "Expected deterministic refusal, got %A" outcome)

            Directory.Exists blockedDestination |> should equal true

            match
                SolutionEditScenario.plan
                    workspace
                    "solution.project.rename"
                    (Some one.Node.Id)
                    [ SolutionEditScenario.argument "name" (Text "one") ]
            with
            | Success plan -> plan.FileRename.IsSome |> should equal true
            | Failure failure -> failwithf "Case-only rename was refused: %A" failure

            match
                SolutionEditScenario.plan
                    workspace
                    "solution.folder.remove"
                    (Some source.Node.Id)
                    [ SolutionEditScenario.argument "recursive" (Boolean true) ]
            with
            | Success plan ->
                plan.Request.Intents.Contains WorkspaceEditIntent.RecursiveDelete
                |> should equal true
            | Failure failure -> failwithf "Recursive metadata removal was refused: %A" failure
        finally
            SolutionEditScenario.delete directory
            SolutionEditScenario.delete foreignDirectory
