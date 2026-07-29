namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions

open System.Collections.Immutable

module internal ProjectFolderCommands =
    let private parameter id parameterType required name =
        CommandParameterDescriptor.Create(
            CommandParameterId.Create id,
            parameterType,
            required,
            name
        )

    let private command id name parameters =
        CommandDescriptor.Create(
            CommandId.Create id,
            name,
            CommandAccess.Write,
            parameters,
            [ WorkspaceNodeKind.Project ]
        )

    let all =
        ImmutableArray.CreateRange
            [ command
                  "project.folder.new"
                  "Create project folder"
                  [ parameter "path" CommandParameterType.Path true "Path" ]
              command
                  "project.folder.copy"
                  "Copy project folder"
                  [ parameter "source" CommandParameterType.Path true "Source"
                    parameter "path" CommandParameterType.Path true "Destination" ]
              command
                  "project.folder.link"
                  "Link project folder"
                  [ parameter "source" CommandParameterType.Path true "Source"
                    parameter "path" CommandParameterType.Path true "Link path"
                    parameter "itemType" CommandParameterType.Choice true "Item type" ]
              command
                  "project.folder.rename"
                  "Rename project folder"
                  [ parameter "path" CommandParameterType.Path true "Path"
                    parameter "name" CommandParameterType.Text true "Name" ]
              command
                  "project.folder.move"
                  "Move project folder"
                  [ parameter "path" CommandParameterType.Path true "Path"
                    parameter "destination" CommandParameterType.Path true "Destination" ]
              command
                  "project.folder.remove"
                  "Remove project folder"
                  [ parameter "path" CommandParameterType.Path true "Path" ]
              command
                  "project.folder.delete"
                  "Delete project folder"
                  [ parameter "path" CommandParameterType.Path true "Path" ] ]

    let tryDescribe id =
        all |> Seq.tryFind (fun descriptor -> descriptor.Id = id)

    let discover (workspace: SolutionWorkspace) targetNodeId =
        if workspace.Descriptor.IsReadOnly then
            ImmutableArray<CommandDescriptor>.Empty
        else
            targetNodeId
            |> Option.bind (fun id ->
                workspace.Contents.Projects
                |> Seq.tryFind (fun project ->
                    project.Node.Id = id && project.Node.Supports WorkspaceCapabilityId.Write)
                |> Option.map (fun _ -> all))
            |> Option.defaultValue ImmutableArray<CommandDescriptor>.Empty
