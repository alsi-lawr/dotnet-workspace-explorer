namespace Dotnet.WorkspaceExplorer.Packages

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.PackageExplorer")>]
[<assembly: InternalsVisibleTo("Dotnet.WorkspaceExplorer.PackageExplorer.UnitTests")>]
do ()
