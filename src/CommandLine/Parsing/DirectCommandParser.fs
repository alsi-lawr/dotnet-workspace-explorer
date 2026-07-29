namespace Dotnet.WorkspaceExplorer.CommandLine


#nowarn "3261"
#nowarn "3511"

open System

module internal DirectCommandParser =
    let private help tokens =
        tokens
        |> List.exists (fun token -> token = "--help" || token = "-h" || token = "-?")

    let private splitSentinel tokens =
        match tokens |> List.tryFindIndex ((=) "--") with
        | Some index -> tokens |> List.take index, tokens |> List.skip index
        | None -> tokens, []

    let parse (arguments: string array) =
        let json, child =
            match arguments |> Array.toList with
            | "--json" :: tail -> true, tail
            | tail -> false, tail

        let beforeSentinel, sentinel = splitSentinel child

        let sentinelOperands =
            match sentinel with
            | [] -> []
            | _ :: tail -> tail

        let parsed =
            match child with
            | [] -> Error(DirectCommandFailures.invalid "A Workspace Explorer command is required.")
            | command :: _ ->
                match command with
                | "solution"
                | "sln" ->
                    let _, positions, unknown = SolutionCommandParser.scan beforeSentinel.Tail

                    match positions, unknown, sentinelOperands with
                    | target :: "launch" :: "list" :: [], [], [] ->
                        Ok(LaunchProfile(target, LaunchList, None, [], help beforeSentinel))
                    | target :: "launch" :: "set" :: name :: projects, [], [] ->
                        Ok(
                            LaunchProfile(
                                target,
                                LaunchSet,
                                Some name,
                                projects,
                                help beforeSentinel
                            )
                        )
                    | target :: "launch" :: "remove" :: name :: [], [], [] ->
                        Ok(LaunchProfile(target, LaunchRemove, Some name, [], help beforeSentinel))
                    | _ ->
                        let operationIndex =
                            positions
                            |> List.tryFindIndex (fun value ->
                                [ "add"; "list"; "remove"; "migrate" ] |> List.contains value)

                        let target, operation, operands =
                            match operationIndex with
                            | Some index ->
                                let before = positions |> List.take index
                                let after = positions |> List.skip (index + 1)
                                let target = if before.Length = 1 then Some before.Head else None

                                let operation =
                                    [ Add; List; Remove; Migrate ][positions[index]
                                                                   |> function
                                                                       | "add" -> 0
                                                                       | "list" -> 1
                                                                       | "remove" -> 2
                                                                       | _ -> 3]

                                target, Some operation, after @ sentinelOperands
                            | None ->
                                match positions with
                                | [ target ] -> Some target, None, []
                                | _ -> None, None, []

                        if
                            not (List.isEmpty unknown)
                            && operation |> Option.exists (fun value -> value <> List)
                        then
                            Error(
                                DirectCommandFailures.invalid
                                    "Unknown solution option prevents verification."
                            )
                        else
                            Ok(Solution(target, operation, operands, help beforeSentinel))
                | "package" ->
                    let options, positions, unknown = PackageCommandParser.scan beforeSentinel.Tail

                    let operation, operands =
                        match positions with
                        | op :: tail ->
                            let parsed =
                                match op with
                                | "add" -> Some PackageAdd
                                | "list" -> Some PackageList
                                | "remove" -> Some PackageRemove
                                | "update" -> Some PackageUpdate
                                | "search" -> Some PackageSearch
                                | "download" -> Some PackageDownload
                                | _ -> None

                            parsed, tail @ sentinelOperands
                        | [] -> None, []

                    let mutatingOperandsAmbiguous =
                        operation
                        |> Option.exists (fun value ->
                            value = PackageAdd || value = PackageRemove || value = PackageUpdate)
                        && operands.Length > 1

                    Ok(
                        Package(
                            operation,
                            options |> Map.tryFind "--project",
                            options |> Map.tryFind "--file",
                            options
                            |> Map.tryFind "--version"
                            |> Option.orElseWith (fun () -> options |> Map.tryFind "-v"),
                            options
                            |> Map.tryFind "--framework"
                            |> Option.orElseWith (fun () -> options |> Map.tryFind "-f"),
                            operands,
                            not (List.isEmpty unknown) || mutatingOperandsAmbiguous,
                            help beforeSentinel
                        )
                    )
                | "reference" ->
                    let options, positions, unknown =
                        ReferenceCommandParser.scan beforeSentinel.Tail

                    let operation, operands =
                        match positions with
                        | op :: tail ->
                            let parsed =
                                match op with
                                | "add" -> Some ReferenceAdd
                                | "list" -> Some ReferenceList
                                | "remove" -> Some ReferenceRemove
                                | _ -> None

                            parsed, tail @ sentinelOperands
                        | [] -> None, []

                    Ok(
                        Reference(
                            operation,
                            options |> Map.tryFind "--project",
                            options
                            |> Map.tryFind "--framework"
                            |> Option.orElseWith (fun () -> options |> Map.tryFind "-f"),
                            operands,
                            not (List.isEmpty unknown),
                            help beforeSentinel
                        )
                    )
                | "new" ->
                    let options, positions, _ = TemplateCommandParser.scan beforeSentinel.Tail

                    let optionEnabled name =
                        options
                        |> Map.tryFind name
                        |> Option.exists (fun value ->
                            not (String.Equals(value, "false", StringComparison.OrdinalIgnoreCase)))

                    let operation, operands =
                        match positions with
                        | "list" :: tail -> TemplateList, tail @ sentinelOperands
                        | "search" :: tail -> TemplateSearch, tail @ sentinelOperands
                        | "details" :: tail -> TemplateDetails, tail @ sentinelOperands
                        | "install" :: tail -> TemplateInstall, tail @ sentinelOperands
                        | "uninstall" :: tail -> TemplateUninstall, tail @ sentinelOperands
                        | "update" :: tail -> TemplateUpdate, tail @ sentinelOperands
                        | "create" :: tail -> TemplateCreate, tail @ sentinelOperands
                        | [] when Map.isEmpty options && List.isEmpty sentinelOperands ->
                            TemplateList, []
                        | tail -> TemplateCreate, tail @ sentinelOperands

                    Ok(
                        New(
                            operation,
                            options
                            |> Map.tryFind "--output"
                            |> Option.orElseWith (fun () -> options |> Map.tryFind "-o"),
                            optionEnabled "--dry-run" || optionEnabled "--check-only",
                            operands,
                            help beforeSentinel
                        )
                    )
                | "restore"
                | "build"
                | "test"
                | "run" -> Ok(Lifecycle(command, help beforeSentinel))
                | _ ->
                    Error(
                        DirectCommandFailures.invalid
                            "This dotnet command is not supported by the Workspace Explorer command grammar."
                    )

        json, child, parsed

    let commandId =
        function
        | Solution _ -> "solution"
        | Package _ -> "package"
        | Reference _ -> "reference"
        | New _ -> "new"
        | Lifecycle(command, _) -> command
        | LaunchProfile _ -> "solution.launch"

    let mutates =
        function
        | Solution(_, Some(Add | Remove | Migrate), _, false) -> true
        | Package(Some(PackageAdd | PackageRemove | PackageUpdate), _, _, _, _, _, _, false) -> true
        | Reference(Some(ReferenceAdd | ReferenceRemove), _, _, _, _, false) -> true
        | New(TemplateCreate, _, false, _, false) -> true
        | _ -> false
