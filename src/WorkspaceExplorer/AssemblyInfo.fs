namespace Dotnet.WorkspaceExplorer

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests")>]
[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.PackageExplorer.UnitTests")>]
do ()
