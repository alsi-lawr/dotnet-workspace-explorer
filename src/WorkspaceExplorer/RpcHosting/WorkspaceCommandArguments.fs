namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc

open System
open System.Collections.Immutable
open System.IO

module internal WorkspaceCommandArguments =
    let commandTarget (workspace: SolutionWorkspace) targetNodeId =
        match targetNodeId with
        | None -> Ok None
        | Some value when value = workspace.Descriptor.Id.Value -> Ok None
        | Some value ->
            workspace.Contents.Nodes
            |> Seq.tryFind (fun node -> node.Id.Value = value)
            |> Option.map (fun node -> Ok(Some node.Id))
            |> Option.defaultValue (
                Error(RpcErrors.create "not_found" "The command target was not found." None)
            )

    let commandArguments
        (workspace: SolutionWorkspace)
        (descriptor: CommandDescriptor)
        (value: RpcValue)
        =
        try
            let fields = RpcValue.requireMap "arguments" value

            RpcValue.ensureOnly "arguments" (descriptor.Parameters |> Seq.map _.Id.Value) fields

            let solutionDirectory =
                Path.GetDirectoryName workspace.SolutionPath.Value
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let arguments =
                descriptor.Parameters
                |> Seq.choose (fun parameter ->
                    match RpcValue.optionalField parameter.Id.Value fields with
                    | None when parameter.Required ->
                        invalidArg "arguments" $"Missing required argument '{parameter.Id.Value}'."
                    | None -> None
                    | Some raw ->
                        let parsed =
                            match parameter.Type with
                            | CommandParameterType.Text ->
                                Text(RpcValue.requireString parameter.Id.Value raw)
                            | CommandParameterType.Path ->
                                let path = RpcValue.requireString parameter.Id.Value raw

                                CommandParameterValue.Path(
                                    WorkspaceArtifactPath.Create(
                                        Path.GetFullPath(path, solutionDirectory)
                                    )
                                )
                            | CommandParameterType.Boolean ->
                                match raw with
                                | RpcValue.Boolean value -> CommandParameterValue.Boolean value
                                | _ -> invalidArg parameter.Id.Value "Expected a boolean."
                            | CommandParameterType.NodeId ->
                                let nodeId = RpcValue.requireString parameter.Id.Value raw

                                match
                                    workspace.Contents.Nodes
                                    |> Seq.tryFind (fun node -> node.Id.Value = nodeId)
                                with
                                | Some node -> Node node.Id
                                | None ->
                                    invalidArg
                                        parameter.Id.Value
                                        "The node argument was not found."
                            | CommandParameterType.Integer ->
                                Integer(RpcValue.requireInteger parameter.Id.Value raw)
                            | CommandParameterType.Choice ->
                                Choice(
                                    RpcValue.requireString parameter.Id.Value raw
                                    |> CommandChoiceId.Create
                                )
                            | CommandParameterType.TextArray ->
                                TextArray(
                                    RpcValue.requireArray parameter.Id.Value raw
                                    |> Seq.map (RpcValue.requireString parameter.Id.Value)
                                    |> ImmutableArray.CreateRange
                                )
                            | _ ->
                                invalidArg parameter.Id.Value "Unsupported command parameter type."

                        Some
                            { ParameterId = parameter.Id
                              Value = parsed })
                |> CommandArguments.Create

            Ok arguments
        with :? ArgumentException as error ->
            Error(RpcErrors.invalidParams error.Message)
