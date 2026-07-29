namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open System.Collections.Immutable
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions

module internal ProjectPropertyCommands =
    let private parameter id parameterType required name =
        CommandParameterDescriptor.Create(
            CommandParameterId.Create id,
            parameterType,
            required,
            name
        )

    let descriptor =
        CommandDescriptor.Create(
            CommandId.Create "project.property.set",
            "Set project property",
            CommandAccess.Write,
            [ parameter "name" CommandParameterType.Choice true "Property name"
              parameter "value" CommandParameterType.Text true "Value"
              parameter "scope" CommandParameterType.Path false "Writable project or import file"
              parameter
                  "condition"
                  CommandParameterType.Text
                  false
                  "Property group condition (empty for unconditional)" ],
            [ WorkspaceNodeKind.Project ]
        )

    let all = ImmutableArray.Create descriptor

    let tryDescribe id =
        if descriptor.Id = id then Some descriptor else None

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
