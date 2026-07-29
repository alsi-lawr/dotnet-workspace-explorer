namespace Dotnet.WorkspaceExplorer.CommandLine


#nowarn "3261"
#nowarn "3511"

open System
open System.Text.RegularExpressions

type internal FileBasedPackageDirective = { Id: string; Version: string option }

module internal FileBasedPackageDirectives =
    let private directive =
        Regex("^\\s*#:\\s*package\\s+([^@\\s]+)(?:@([^\\s]+))?\\s*$", RegexOptions.CultureInvariant)

    let private prefix = Regex("^\\s*#:\\s*package\\b", RegexOptions.CultureInvariant)

    let Parse (source: string) =
        if isNull source then
            Error(DirectCommandFailures.invalid "Package source text is required.")
        else
            source.Replace("\r\n", "\n").Split '\n'
            |> Array.fold
                (fun state line ->
                    match state with
                    | Error failure -> Error failure
                    | Ok directives ->
                        let matched = directive.Match line

                        if matched.Success then
                            let version = matched.Groups[2].Value

                            Ok(
                                { Id = matched.Groups[1].Value
                                  Version =
                                    if String.IsNullOrWhiteSpace version then
                                        None
                                    else
                                        Some version }
                                :: directives
                            )
                        elif prefix.IsMatch line then
                            Error(
                                DirectCommandFailures.invalid
                                    "A file-based package directive is malformed."
                            )
                        else
                            Ok directives)
                (Ok [])
            |> Result.map List.rev

    let Contains (id: string, version: string option, directives: FileBasedPackageDirective list) =
        directives
        |> List.exists (fun directive ->
            String.Equals(directive.Id, id, StringComparison.OrdinalIgnoreCase)
            && version |> Option.forall (fun expected -> directive.Version = Some expected))
