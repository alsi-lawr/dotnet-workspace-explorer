namespace Dotnet.WorkspaceExplorer.CommandLine

#nowarn "3261"
#nowarn "3511"

module internal DirectCommandParser =
    let parse (arguments: string array) =
        let json, command =
            match arguments |> Array.toList with
            | "--json" :: tail -> true, tail
            | tail -> false, tail

        let parsed =
            match command with
            | ("solution" | "sln") :: target :: "launch" :: "list" :: [] ->
                Ok(LaunchProfile(target, LaunchList, None, []))
            | ("solution" | "sln") :: target :: "launch" :: "set" :: name :: projects ->
                Ok(LaunchProfile(target, LaunchSet, Some name, projects))
            | ("solution" | "sln") :: target :: "launch" :: "remove" :: name :: [] ->
                Ok(LaunchProfile(target, LaunchRemove, Some name, []))
            | ("solution" | "sln") :: solution :: "add" :: ("directory" | "dir") :: directory :: [] ->
                Ok(ImportDirectory(solution, directory))
            | _ ->
                Error(
                    DirectCommandFailures.invalid "The Workspace Explorer command is not supported."
                )

        json, parsed
