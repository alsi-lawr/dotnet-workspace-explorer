namespace Dotnet.WorkspaceExplorer.WorkspaceCommands

open System.Collections.Immutable
open Dotnet.WorkspaceExplorer.Workspaces

[<RequireQualifiedAccess>]
module internal ContextWorkspaceCommands =
    let private parameter id name =
        CommandParameterDescriptor.Create(
            CommandParameterId.Create id,
            CommandParameterType.Text,
            true,
            name
        )

    let private typedParameter id parameterType name =
        CommandParameterDescriptor.Create(CommandParameterId.Create id, parameterType, true, name)

    let create =
        CommandDescriptor.Create(
            CommandId.Create "workspace.create",
            "New",
            CommandAccess.Write,
            [ parameter "selectionId" "Template or empty-file selection"
              parameter "name" "Name" ],
            [ WorkspaceNodeKind.Workspace
              WorkspaceNodeKind.SolutionFolder
              WorkspaceNodeKind.SolutionItem
              WorkspaceNodeKind.Project
              WorkspaceNodeKind.ProjectFolder
              WorkspaceNodeKind.ProjectFile
              WorkspaceNodeKind.DependencyContainer
              WorkspaceNodeKind.Dependency
              WorkspaceNodeKind.DependencyProperty ]
        )

    let delete =
        CommandDescriptor.Create(
            CommandId.Create "workspace.delete",
            "Delete",
            CommandAccess.Write,
            [],
            [ WorkspaceNodeKind.SolutionFolder
              WorkspaceNodeKind.SolutionItem
              WorkspaceNodeKind.Project
              WorkspaceNodeKind.ProjectFolder
              WorkspaceNodeKind.ProjectFile ]
        )

    let rename =
        CommandDescriptor.Create(
            CommandId.Create "workspace.rename",
            "Rename",
            CommandAccess.Write,
            [ typedParameter "name" CommandParameterType.Text "Name" ],
            [ WorkspaceNodeKind.SolutionFolder
              WorkspaceNodeKind.SolutionItem
              WorkspaceNodeKind.Project
              WorkspaceNodeKind.ProjectFolder
              WorkspaceNodeKind.ProjectFile ]
        )

    let move =
        CommandDescriptor.Create(
            CommandId.Create "workspace.move",
            "Move",
            CommandAccess.Write,
            [ typedParameter
                  "sourceNodeIds"
                  CommandParameterType.NodeIdArray
                  "Source workspace nodes" ],
            [ WorkspaceNodeKind.Workspace
              WorkspaceNodeKind.SolutionFolder
              WorkspaceNodeKind.SolutionItem
              WorkspaceNodeKind.Project
              WorkspaceNodeKind.ProjectFolder
              WorkspaceNodeKind.ProjectFile
              WorkspaceNodeKind.DependencyContainer
              WorkspaceNodeKind.Dependency
              WorkspaceNodeKind.DependencyProperty ]
        )

    let copy =
        CommandDescriptor.Create(
            CommandId.Create "workspace.copy",
            "Copy",
            CommandAccess.Write,
            [ typedParameter
                  "sourceNodeIds"
                  CommandParameterType.NodeIdArray
                  "Source workspace nodes" ],
            [ WorkspaceNodeKind.Project
              WorkspaceNodeKind.ProjectFolder
              WorkspaceNodeKind.ProjectFile
              WorkspaceNodeKind.DependencyContainer
              WorkspaceNodeKind.Dependency
              WorkspaceNodeKind.DependencyProperty ]
        )

    let all = ImmutableArray.Create(create, delete, rename, move, copy)

    let tryDescribe id =
        all |> Seq.tryFind (fun descriptor -> descriptor.Id = id)

    let discover readOnly (target: WorkspaceNode option) =
        if
            readOnly
            || target
               |> Option.exists (fun node -> node.LoadState = WorkspaceNodeLoadState.FilteredOut)
        then
            ImmutableArray<CommandDescriptor>.Empty
        else
            target
            |> Option.map (fun node ->
                all
                |> Seq.filter (fun descriptor -> descriptor.TargetKinds.Contains node.Kind)
                |> ImmutableArray.CreateRange)
            |> Option.defaultValue ImmutableArray<CommandDescriptor>.Empty
