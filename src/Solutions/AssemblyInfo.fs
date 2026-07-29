namespace Dotnet.WorkspaceExplorer.Solutions

open Dotnet.WorkspaceExplorer.Workspaces

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.WorkspaceIndex")>]
[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.WorkspaceCommands")>]
[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.CommandLine")>]
do ()
