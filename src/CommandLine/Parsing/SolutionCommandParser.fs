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

module internal SolutionCommandParser =
    let scan =
        CommandOptionScanner.scan
            (Set.ofList [ "--solution-folder"; "-s" ])
            (Set.ofList [ "--in-root" ])
            (Set.ofList [ "--include-references" ])
