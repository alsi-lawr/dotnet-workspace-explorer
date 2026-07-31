namespace Dotnet.WorkspaceExplorer

open System
open System.Collections.Generic
open System.IO
open Dotnet.WorkspaceExplorer.Rpc

[<RequireQualifiedAccess>]
module internal WorkspaceGitStatusParsing =
    let parsePorcelain root (value: string) =
        try
            let records = value.Split('\000', StringSplitOptions.RemoveEmptyEntries)
            let values = ResizeArray<GitDecorationState * string>()
            let mutable index = 0

            while index < records.Length do
                let record = records[index]

                if record.Length < 4 || record[2] <> ' ' then
                    invalidArg (nameof value) "Git returned malformed porcelain output."

                let status = record[..1]
                let path = record[3..]

                let state =
                    if status = "??" || status.Contains 'A' then
                        Added
                    else
                        Changed

                values.Add(state, Path.GetFullPath(path, root))

                if status.Contains 'R' || status.Contains 'C' then
                    index <- index + 1

                    if index >= records.Length then
                        invalidArg (nameof value) "Git returned an incomplete rename record."

                    values.Add(state, Path.GetFullPath(records[index], root))

                index <- index + 1

            Ok(values.ToArray())
        with
        | :? ArgumentException
        | :? NotSupportedException
        | :? PathTooLongException ->
            Error(
                RpcErrors.create "git_parse_failed" "Git returned malformed porcelain output." None
            )
