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

module internal TemplateCommandParser =
    let scan =
        CommandOptionScanner.scan
            (Set.ofList
                [ "--output"
                  "-o"
                  "--name"
                  "-n"
                  "--project"
                  "--verbosity"
                  "-v"
                  "--add-source"
                  "--nuget-source" ])
            (Set.ofList [ "--force"; "--no-update-check"; "--diagnostics"; "-d" ])
            (Set.ofList [ "--dry-run"; "--check-only" ])
