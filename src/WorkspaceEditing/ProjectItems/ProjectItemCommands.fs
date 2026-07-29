namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions

open System.Collections.Immutable

module internal ProjectItemCommands =
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
                  "project.item.add"
                  "Add project item"
                  [ parameter "path" CommandParameterType.Path true "Path"
                    parameter "itemType" CommandParameterType.Choice true "Item type"
                    parameter "link" CommandParameterType.Boolean false "Link external item" ]
              command
                  "project.item.new"
                  "Create project item"
                  [ parameter "path" CommandParameterType.Path true "Path"
                    parameter "itemType" CommandParameterType.Choice true "Item type"
                    parameter "contents" CommandParameterType.Text false "Contents" ]
              command
                  "project.item.copy"
                  "Copy project item"
                  [ parameter "source" CommandParameterType.Path true "Source"
                    parameter "path" CommandParameterType.Path true "Destination"
                    parameter "itemType" CommandParameterType.Choice true "Item type" ]
              command
                  "project.item.rename"
                  "Rename project item"
                  [ parameter "path" CommandParameterType.Path true "Path"
                    parameter "name" CommandParameterType.Text true "Name" ]
              command
                  "project.item.move"
                  "Move project item"
                  [ parameter "path" CommandParameterType.Path true "Path"
                    parameter "destination" CommandParameterType.Path true "Destination" ]
              command
                  "project.item.remove"
                  "Remove project item"
                  [ parameter "path" CommandParameterType.Path true "Path" ]
              command
                  "project.item.delete"
                  "Delete project item"
                  [ parameter "path" CommandParameterType.Path true "Path" ]
              command
                  "project.item.set-build-action"
                  "Set project item build action"
                  [ parameter "path" CommandParameterType.Path true "Path"
                    parameter "itemType" CommandParameterType.Choice true "Item type" ]
              command
                  "project.item.set-metadata"
                  "Set project item metadata"
                  [ parameter "path" CommandParameterType.Path true "Path"
                    parameter "name" CommandParameterType.Choice true "Metadata name"
                    parameter "value" CommandParameterType.Text true "Value" ]
              command
                  "project.relocate"
                  "Move project directory"
                  [ parameter
                        "destination"
                        CommandParameterType.Path
                        true
                        "New destination directory"
                    parameter "folder" CommandParameterType.NodeId false "Solution folder" ]
              command
                  "project.property.set"
                  "Set project property"
                  [ parameter "name" CommandParameterType.Choice true "Property name"
                    parameter "value" CommandParameterType.Text true "Value"
                    parameter
                        "scope"
                        CommandParameterType.Path
                        false
                        "Writable project or import file"
                    parameter
                        "condition"
                        CommandParameterType.Text
                        false
                        "Property group condition (empty for unconditional)" ] ]


    let tryDescribe id =
        all |> Seq.tryFind (fun descriptor -> descriptor.Id = id)

    let discover (workspace: SolutionWorkspace) targetNodeId =
        if workspace.Descriptor.IsReadOnly then
            ImmutableArray<CommandDescriptor>.Empty
        else
            targetNodeId
            |> Option.bind (fun id ->
                workspace.Contents.Projects
                |> Seq.tryFind (fun project -> project.Node.Id = id)
                |> Option.map (fun _ -> all))
            |> Option.defaultValue ImmutableArray<CommandDescriptor>.Empty
