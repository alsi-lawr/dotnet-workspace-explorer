namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System.IO
open Dotnet.WorkspaceExplorer.Solutions
open FsUnit.Xunit
open Xunit

[<Collection("Launch-profile scenarios")>]
type LegacyDirectoryImportTests() =
    [<Theory>]
    [<InlineData(".sln", "directory")>]
    [<InlineData(".slnx", "dir")>]
    member _.``adding a nested folder through legacy directory aliases persists it without invoking dotnet and rejects missing folders``
        (extension: string, alias: string)
        =
        let directory = DirectCommandProcess.temporaryDirectory "direct command-legacy"

        try
            let solution = Path.Combine(directory, $"Demo{extension}")
            let folder = Directory.CreateDirectory(Path.Combine(directory, "src", "nested"))
            let marker = Path.Combine(directory, "launched")
            DirectCommandProcess.saveSolution solution []

            let result =
                DirectCommandProcess.run
                    directory
                    "marker"
                    [ "--json"; "sln"; solution; "add"; alias; folder.FullName ]
                    [ "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_STARTED_PATH", marker ]

            (DirectCommandProcess.success result) |> should equal true
            (File.Exists marker) |> should equal false

            match
                Dotnet.WorkspaceExplorer.Solutions.SolutionWorkspaceReader
                    .OpenAsync(solution)
                    .Result
            with
            | Dotnet.WorkspaceExplorer.Workspaces.Success workspace ->
                (workspace.Contents.Folders)
                |> Seq.exists (fun item -> item.Path = "/src/nested/")
                |> should equal true
            | outcome -> failwithf "Expected the persisted folder, got %A" outcome

            let refused =
                DirectCommandProcess.run
                    directory
                    "marker"
                    [ "--json"
                      "solution"
                      solution
                      "add"
                      "directory"
                      Path.Combine(directory, "missing") ]
                    [ "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_STARTED_PATH", marker ]

            (DirectCommandProcess.success refused) |> should equal false

            (File.Exists marker) |> should equal false
        finally
            DirectCommandProcess.delete directory
