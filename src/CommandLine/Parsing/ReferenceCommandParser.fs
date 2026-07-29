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

module internal ReferenceCommandParser =
    let scan =
        CommandOptionScanner.scan
            (Set.ofList [ "--project"; "--framework"; "-f" ])
            (Set.ofList [ "--interactive"; "--no-restore" ])
            Set.empty
