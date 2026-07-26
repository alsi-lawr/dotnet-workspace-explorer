namespace Dotnet.CLI.Plus

open System
open System.Collections.Immutable
open System.IO
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution
open Dotnet.CLI.Plus.Transport

module internal PipeCommandProtocol =
    let commandTarget (workspace: SolutionWorkspace) targetId =
        match targetId with
        | None -> Ok None
        | Some value when value = workspace.WorkspaceDescriptor.WorkspaceId.Value -> Ok None
        | Some value ->
            workspace.RootProjection.Nodes
            |> Seq.tryFind (fun node -> node.NodeId.Value = value)
            |> Option.map (fun node -> Ok(Some node.NodeId))
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

            RpcValue.ensureOnly
                "arguments"
                (descriptor.ParameterDescriptors |> Seq.map _.ParameterId.Value)
                fields

            let solutionDirectory =
                Path.GetDirectoryName workspace.BackingPath.Value
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let arguments =
                descriptor.ParameterDescriptors
                |> Seq.choose (fun parameter ->
                    match RpcValue.optionalField parameter.ParameterId.Value fields with
                    | None when parameter.Required ->
                        invalidArg
                            "arguments"
                            $"Missing required argument '{parameter.ParameterId.Value}'."
                    | None -> None
                    | Some raw ->
                        let parsed =
                            match parameter.ParameterType with
                            | CommandParameterType.Text ->
                                Text(RpcValue.requireString parameter.ParameterId.Value raw)
                            | CommandParameterType.Path ->
                                let path = RpcValue.requireString parameter.ParameterId.Value raw

                                Path(
                                    WorkspaceArtifactPath.Create(
                                        Path.GetFullPath(path, solutionDirectory)
                                    )
                                )
                            | CommandParameterType.Boolean ->
                                match raw with
                                | RpcValue.Boolean value -> Boolean value
                                | _ -> invalidArg parameter.ParameterId.Value "Expected a boolean."
                            | CommandParameterType.NodeId ->
                                let nodeId = RpcValue.requireString parameter.ParameterId.Value raw

                                match
                                    workspace.RootProjection.Nodes
                                    |> Seq.tryFind (fun node -> node.NodeId.Value = nodeId)
                                with
                                | Some node -> Node node.NodeId
                                | None ->
                                    invalidArg
                                        parameter.ParameterId.Value
                                        "The node argument was not found."
                            | CommandParameterType.Integer ->
                                Integer(RpcValue.requireInteger parameter.ParameterId.Value raw)
                            | CommandParameterType.Choice ->
                                Choice(
                                    RpcValue.requireString parameter.ParameterId.Value raw
                                    |> CommandChoiceId.Create
                                )
                            | CommandParameterType.TextArray ->
                                TextArray(
                                    RpcValue.requireArray parameter.ParameterId.Value raw
                                    |> Seq.map (RpcValue.requireString parameter.ParameterId.Value)
                                    |> ImmutableArray.CreateRange
                                )
                            | _ ->
                                invalidArg
                                    parameter.ParameterId.Value
                                    "Unsupported command parameter type."

                        Some
                            { ParameterId = parameter.ParameterId
                              Value = parsed })
                |> CommandArguments.Create

            Ok arguments
        with :? ArgumentException as error ->
            Error(RpcErrors.invalidParams error.Message)
