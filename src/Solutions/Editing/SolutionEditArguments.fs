namespace Dotnet.WorkspaceExplorer.Solutions

open System
open Dotnet.WorkspaceExplorer.Workspaces

module internal SolutionEditArguments =
    let argument argumentId (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryPick (fun candidate ->
            if candidate.ParameterId.Value = argumentId then
                Some candidate.Value
            else
                None)

    let requiredText name arguments =
        match argument name arguments with
        | Some(Text value) when not (String.IsNullOrWhiteSpace value) -> Ok value
        | _ -> Error $"'{name}' is required."

    let requiredPath name arguments =
        match argument name arguments with
        | Some(Path value) -> Ok value
        | _ -> Error $"'{name}' is required."

    let optionalBoolean name defaultValue arguments =
        match argument name arguments with
        | None -> Ok defaultValue
        | Some(Boolean value) -> Ok value
        | _ -> Error $"'{name}' must be a boolean."

    let requiredBoolean name arguments =
        match argument name arguments with
        | Some(Boolean value) -> Ok value
        | _ -> Error $"'{name}' is required."

    let requiredNode name arguments =
        match argument name arguments with
        | Some(Node value) -> Ok value
        | _ -> Error $"'{name}' is required."

    let optionalNode name arguments =
        match argument name arguments with
        | None -> Ok None
        | Some(Node value) -> Ok(Some value)
        | _ -> Error $"'{name}' must be a node ID."

    let validateArguments (descriptor: CommandDescriptor) (arguments: CommandArguments) =
        let invalidArgument =
            arguments.Values
            |> Seq.tryPick (fun value ->
                match
                    descriptor.Parameters
                    |> Seq.tryFind (fun expected -> expected.Id = value.ParameterId)
                with
                | None -> Some $"Unknown argument '{value.ParameterId.Value}'."
                | Some expected ->
                    let valid =
                        match expected.Type, value.Value with
                        | CommandParameterType.Text, Text text ->
                            not (String.IsNullOrWhiteSpace text)
                        | CommandParameterType.Path, Path _
                        | CommandParameterType.Boolean, Boolean _
                        | CommandParameterType.Integer, Integer _
                        | CommandParameterType.NodeId, Node _
                        | CommandParameterType.Choice, Choice _ -> true
                        | _ -> false

                    if valid then
                        None
                    else
                        Some $"Argument '{value.ParameterId.Value}' has the wrong type or value.")

        match invalidArgument with
        | Some error -> Error error
        | None ->
            descriptor.Parameters
            |> Seq.tryFind (fun expected ->
                expected.Required
                && arguments.Values
                   |> Seq.exists (fun value -> value.ParameterId = expected.Id)
                   |> not)
            |> Option.map (fun missing -> Error $"'{missing.Id.Value}' is required.")
            |> Option.defaultValue (Ok())
