namespace Dotnet.WorkspaceExplorer.CommandLine

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceEditing

#nowarn "3261"
#nowarn "3511"

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

module internal CommandOptionScanner =
    let scan
        (values: Set<string>)
        (flags: Set<string>)
        (optionalBooleans: Set<string>)
        (tokens: string list)
        =
        let rec collect
            (remaining: string list)
            (collected: Map<string, string>)
            (positional: string list)
            (unknown: string list)
            =
            match remaining with
            | [] -> collected, List.rev positional, List.rev unknown
            | ("--help" | "-h" | "-?") :: tail -> collect tail collected positional unknown
            | token :: tail when
                token.StartsWith("--", StringComparison.Ordinal)
                && token.Contains("=", StringComparison.Ordinal)
                ->
                let name, value = token.Split('=', 2) |> fun parts -> parts[0], parts[1]

                if values |> Set.contains name || optionalBooleans |> Set.contains name then
                    collect tail (Map.add name value collected) positional unknown
                elif flags |> Set.contains name then
                    collect tail collected positional unknown
                else
                    collect tail collected positional (name :: unknown)
            | token :: value :: tail when
                optionalBooleans |> Set.contains token
                && (String.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                ->
                collect tail (Map.add token value collected) positional unknown
            | token :: value :: tail when values |> Set.contains token ->
                collect tail (Map.add token value collected) positional unknown
            | token :: tail when optionalBooleans |> Set.contains token ->
                collect tail (Map.add token "true" collected) positional unknown
            | token :: tail when flags |> Set.contains token ->
                collect tail collected positional unknown
            | token :: tail when token.StartsWith("-", StringComparison.Ordinal) ->
                collect tail collected positional (token :: unknown)
            | token :: tail -> collect tail collected (token :: positional) unknown

        collect tokens Map.empty [] []
