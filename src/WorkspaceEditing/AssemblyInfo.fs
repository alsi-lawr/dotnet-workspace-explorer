namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer")>]
[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.WorkspaceCommands")>]
[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.CommandLine")>]
[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.Workspaces.UnitTests")>]
[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests")>]
do ()
