namespace Dotnet.WorkspaceExplorer.CommandLine

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceEditing

#nowarn "3261"
#nowarn "3511"

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type internal TerminalOutputSanitizer() =
    let pending = StringBuilder()

    member _.Push(value: string) =
        pending.Append value |> ignore
        let source = pending.ToString()
        let output = StringBuilder()
        let mutable index = 0
        let mutable incomplete = -1

        while index < source.Length && incomplete < 0 do
            let character = source[index]

            if character = '\u001b' then
                if index + 1 >= source.Length then
                    incomplete <- index
                elif source[index + 1] = '[' then
                    let mutable endIndex = index + 2

                    while endIndex < source.Length
                          && not (source[endIndex] >= '@' && source[endIndex] <= '~') do
                        endIndex <- endIndex + 1

                    if endIndex = source.Length then
                        incomplete <- index
                    else
                        index <- endIndex + 1
                elif source[index + 1] = ']' then
                    let mutable endIndex = index + 2
                    let mutable found = false

                    while endIndex < source.Length && not found do
                        found <-
                            source[endIndex] = '\u0007'
                            || source[endIndex] = '\u001b'
                               && endIndex + 1 < source.Length
                               && source[endIndex + 1] = '\\'

                        endIndex <- endIndex + 1

                    if not found then
                        incomplete <- index
                    else
                        index <- endIndex + (if source[endIndex - 1] = '\u001b' then 1 else 0)
                else
                    index <- index + 2
            else
                if
                    character = '\t'
                    || character = '\n'
                    || character = '\r'
                    || character >= ' ' && character <> '\u007f'
                then
                    output.Append character |> ignore

                index <- index + 1

        pending.Clear() |> ignore

        if incomplete >= 0 then
            pending.Append(source.Substring incomplete) |> ignore

        output.ToString()

    member _.Complete() =
        pending.Clear() |> ignore
        String.Empty
