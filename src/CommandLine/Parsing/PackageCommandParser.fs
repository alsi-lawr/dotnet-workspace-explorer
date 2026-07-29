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

module internal PackageCommandParser =
    let scan =
        CommandOptionScanner.scan
            (Set.ofList
                [ "--project"
                  "--file"
                  "--version"
                  "-v"
                  "--framework"
                  "-f"
                  "--source"
                  "-s"
                  "--configfile"
                  "--package-directory"
                  "--verbosity" ])
            (Set.ofList [ "--prerelease"; "--vulnerable"; "--no-restore"; "-n"; "--interactive" ])
            Set.empty
