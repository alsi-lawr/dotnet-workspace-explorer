namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open System
open System.IO
open System.Threading
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions

module private SolutionScenario =
    let fixturePath name =
        Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "SolutionFiles",
            "CanonicalWorkspace",
            name
        )

    let temporaryDirectory () =
        let path =
            Path.Combine(
                Path.GetTempPath(),
                $"dotnet-workspace-explorer-solution-{Guid.NewGuid():N}"
            )

        Directory.CreateDirectory path |> ignore
        path

    let save path model =
        SolutionSerializers
            .GetSerializerByMoniker(path)
            .SaveAsync(path, model, CancellationToken.None)
            .GetAwaiter()
            .GetResult()

    let openWorkspace path =
        match SolutionWorkspaceReader.OpenAsync(path).Result with
        | Success workspace -> workspace
        | Failure failure -> failwithf "Expected success, got %s" failure.Code.Value

    let delete path =
        if Directory.Exists path then
            Directory.Delete(path, true)
