namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.ProjectEvaluation

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer")>]
[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.WorkspaceEditing")>]
[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.WorkspaceCommands")>]
[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.Workspaces.UnitTests")>]
[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests")>]
do ()
