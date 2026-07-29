namespace Dotnet.WorkspaceExplorer.WorkspaceCommands

open Dotnet.WorkspaceExplorer.Workspaces

module internal DotnetCommandArguments =
    let argument id (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryFind (fun item -> item.ParameterId.Value = id)
        |> Option.map _.Value

    let extraArguments command arguments =
        match argument "arguments" arguments with
        | None -> Ok []
        | Some(TextArray values) when
            (command = "dotnet.build" || command = "dotnet.run")
            && values |> Seq.exists ((=) "--no-restore")
            ->
            Error "Use noRestore instead of --no-restore in arguments."
        | Some(TextArray values) -> Ok(values |> Seq.toList)
        | _ -> Error "arguments must be a text array."

    let noRestoreValue arguments =
        match argument "noRestore" arguments with
        | None -> Ok false
        | Some(Boolean value) -> Ok value
        | _ -> Error "noRestore must be a boolean."
