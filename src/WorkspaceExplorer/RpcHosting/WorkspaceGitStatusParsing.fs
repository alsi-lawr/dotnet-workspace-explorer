namespace Dotnet.WorkspaceExplorer

open System
open System.Collections.Generic
open System.IO
open Dotnet.WorkspaceExplorer.Rpc

[<RequireQualifiedAccess>]
module internal WorkspaceGitStatusParsing =
    let private malformed () =
        Error(RpcErrors.create "git_parse_failed" "Git returned malformed porcelain output." None)

    let private fallbackStates (status: string) =
        let validState value =
            value = ' '
            || value = 'M'
            || value = 'T'
            || value = 'A'
            || value = 'D'
            || value = 'R'
            || value = 'C'
            || value = 'U'

        if
            status.Length <> 2
            || status = "  "
            || not (validState status[0])
            || not (validState status[1])
        then
            malformed ()
        else
            let values = ResizeArray<GitStatusState>()

            if status[0] = 'M' || status[0] = 'T' || status[0] = 'A' || status[0] = 'C' then
                values.Add Staged

            if status[1] = 'M' || status[1] = 'T' || status[1] = 'C' then
                values.Add Unstaged

            if status.Contains 'R' then
                values.Add Renamed

            if status.Contains 'D' then
                values.Add Deleted

            if status.Contains 'U' then
                values.Add Unmerged

            if status[1] = 'A' then
                values.Add Untracked

            match GitStatusStates.normalize values with
            | [||] -> malformed ()
            | states -> Ok states

    let private states status =
        match status with
        | "??" -> Ok [| Untracked |]
        | "AA"
        | "AU" -> Ok [| Unmerged; Untracked |]
        | "UU"
        | "UD"
        | "UA" -> Ok [| Unmerged |]
        | "DA" -> Ok [| Unstaged |]
        | "DD" -> Ok [| Deleted |]
        | "DU" -> Ok [| Deleted; Unmerged |]
        | "!!" -> Ok [| Ignored |]
        | _ -> fallbackStates status

    let private legacyState status =
        if status = "!!" then None
        elif status = "??" || status.Contains 'A' then Some Added
        else Some Changed

    let parsePorcelain root (value: string) =
        try
            let normalizedRoot = Path.GetFullPath root

            if value.Length > 0 && value[value.Length - 1] <> '\000' then
                malformed ()
            else
                let records = value.Split('\000', StringSplitOptions.None)

                let entries =
                    Dictionary<string, GitDecorationState option * HashSet<GitStatusState>>(
                        StringComparer.Ordinal
                    )

                let add status path (states: GitStatusState array) =
                    if String.IsNullOrEmpty path then
                        invalidArg (nameof value) "Git returned an empty porcelain path."

                    let path = Path.GetFullPath(path, normalizedRoot)
                    let legacy = legacyState status

                    match entries.TryGetValue path with
                    | true, (existingLegacy, existingStates) ->
                        for state in states do
                            existingStates.Add state |> ignore

                        match existingLegacy, legacy with
                        | Some Changed, Some Added
                        | None, Some Added
                        | None, Some Changed -> entries[path] <- legacy, existingStates
                        | _ -> ()
                    | _ ->
                        entries.Add(
                            path,
                            (legacy, HashSet<GitStatusState>(states :> seq<GitStatusState>))
                        )

                let mutable index = 0
                let mutable error = None

                while error.IsNone && index < records.Length - 1 do
                    let record = records[index]

                    if record.Length < 4 || record[2] <> ' ' then
                        error <- Some(malformed ())
                    else
                        let status = record[..1]

                        match states status with
                        | Error parseError -> error <- Some(Error parseError)
                        | Ok parsedStates ->
                            add status record[3..] parsedStates

                            if status.Contains 'R' || status.Contains 'C' then
                                index <- index + 1

                                if
                                    index >= records.Length - 1
                                    || String.IsNullOrEmpty records[index]
                                then
                                    error <- Some(malformed ())
                                else
                                    add status records[index] parsedStates

                    index <- index + 1

                match error with
                | Some error -> error
                | None ->
                    entries
                    |> Seq.map (fun (KeyValue(path, (legacy, states))) ->
                        { Path = path
                          States = GitStatusStates.normalize states
                          LegacyState = legacy })
                    |> Seq.sortBy _.Path
                    |> Seq.toArray
                    |> fun entries ->
                        Ok
                            { RepositoryRoot = normalizedRoot
                              Entries = entries }
        with
        | :? ArgumentException
        | :? NotSupportedException
        | :? PathTooLongException -> malformed ()
