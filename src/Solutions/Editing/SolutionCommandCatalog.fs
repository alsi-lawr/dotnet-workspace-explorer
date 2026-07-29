namespace Dotnet.WorkspaceExplorer.Solutions

open System.Collections.Immutable
open Dotnet.WorkspaceExplorer.Workspaces

module internal SolutionCommandCatalog =
    let parameter id parameterType required name =
        CommandParameterDescriptor.Create(
            CommandParameterId.Create id,
            parameterType,
            required,
            name
        )

    let command id name parameters targets =
        CommandDescriptor.Create(
            CommandId.Create id,
            name,
            CommandAccess.Write,
            parameters,
            targets
        )

    let catalog =
        ImmutableArray.CreateRange
            [ command
                  "solution.folder.add"
                  "Add solution folder"
                  [ parameter "name" CommandParameterType.Text true "Name" ]
                  [ WorkspaceNodeKind.Workspace; WorkspaceNodeKind.SolutionFolder ]
              command
                  "solution.folder.import-directory"
                  "Import directory as solution folder"
                  [ parameter "path" CommandParameterType.Path true "Path" ]
                  [ WorkspaceNodeKind.Workspace ]
              command
                  "solution.folder.remove"
                  "Remove solution folder"
                  [ parameter "recursive" CommandParameterType.Boolean false "Recursive" ]
                  [ WorkspaceNodeKind.SolutionFolder ]
              command
                  "solution.item.add"
                  "Add solution item"
                  [ parameter "path" CommandParameterType.Path true "Path" ]
                  [ WorkspaceNodeKind.SolutionFolder ]
              command
                  "solution.item.remove"
                  "Remove solution item"
                  []
                  [ WorkspaceNodeKind.SolutionItem ]
              command
                  "solution.project.add"
                  "Add project"
                  [ parameter "path" CommandParameterType.Path true "Path" ]
                  [ WorkspaceNodeKind.Workspace; WorkspaceNodeKind.SolutionFolder ]
              command "solution.project.remove" "Remove project" [] [ WorkspaceNodeKind.Project ]
              command
                  "solution.project.rename"
                  "Rename project"
                  [ parameter "name" CommandParameterType.Text true "Name" ]
                  [ WorkspaceNodeKind.Project ]
              command
                  "solution.project.move"
                  "Move project"
                  [ parameter "folder" CommandParameterType.NodeId false "Folder" ]
                  [ WorkspaceNodeKind.Project ]
              command
                  "solution.project.update-path"
                  "Update project path"
                  [ parameter "path" CommandParameterType.Path true "Path" ]
                  [ WorkspaceNodeKind.Project ]
              command
                  "solution.build-type.add"
                  "Add build type"
                  [ parameter "name" CommandParameterType.Text true "Name" ]
                  [ WorkspaceNodeKind.Workspace ]
              command
                  "solution.build-type.remove"
                  "Remove build type"
                  []
                  [ WorkspaceNodeKind.Configuration ]
              command
                  "solution.platform.add"
                  "Add platform"
                  [ parameter "name" CommandParameterType.Text true "Name" ]
                  [ WorkspaceNodeKind.Workspace ]
              command "solution.platform.remove" "Remove platform" [] [ WorkspaceNodeKind.Platform ]
              command
                  "solution.project-configuration.set"
                  "Set project configuration"
                  [ parameter
                        "solutionBuildType"
                        CommandParameterType.Text
                        true
                        "Solution build type"
                    parameter "solutionPlatform" CommandParameterType.Text true "Solution platform"
                    parameter "projectBuildType" CommandParameterType.Text true "Project build type"
                    parameter "projectPlatform" CommandParameterType.Text true "Project platform"
                    parameter "builds" CommandParameterType.Boolean true "Builds"
                    parameter "deploys" CommandParameterType.Boolean true "Deploys" ]
                  [ WorkspaceNodeKind.Project ]
              command
                  "solution.project-configuration.remove"
                  "Remove project configuration"
                  [ parameter
                        "solutionBuildType"
                        CommandParameterType.Text
                        true
                        "Solution build type"
                    parameter "solutionPlatform" CommandParameterType.Text true "Solution platform" ]
                  [ WorkspaceNodeKind.Project ]
              command
                  "solution.dependency.add"
                  "Add solution dependency"
                  [ parameter "dependency" CommandParameterType.NodeId true "Dependency" ]
                  [ WorkspaceNodeKind.Project ]
              command
                  "solution.dependency.remove"
                  "Remove solution dependency"
                  [ parameter "dependency" CommandParameterType.NodeId true "Dependency" ]
                  [ WorkspaceNodeKind.Project ] ]


    let descriptor (commandId: CommandId) =
        catalog |> Seq.tryFind (fun candidate -> candidate.Id = commandId)
