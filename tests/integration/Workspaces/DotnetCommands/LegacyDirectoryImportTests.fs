namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System.IO
open System.Text.Json
open Dotnet.WorkspaceExplorer.Solutions
open Xunit

[<Collection("Delegated dotnet processes")>]
type LegacyDirectoryImportTests() =
    [<Theory>]
    [<InlineData(".sln", "directory")>]
    [<InlineData(".slnx", "dir")>]
    member _.``should persist nested folders for legacy directory aliases without invoking dotnet``
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

            Assert.True(DirectCommandProcess.success result)
            Assert.False(File.Exists marker)

            match
                Dotnet.WorkspaceExplorer.Solutions.SolutionWorkspaceReader
                    .OpenAsync(solution)
                    .Result
            with
            | Dotnet.WorkspaceExplorer.Workspaces.Success workspace ->
                Assert.Contains(workspace.Contents.Folders, fun item -> item.Path = "/src/nested/")
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

            Assert.False(DirectCommandProcess.success refused)
            use document = DirectCommandProcess.json refused

            Assert.Equal(
                JsonValueKind.Null,
                document.RootElement.GetProperty("externalExitCode").ValueKind
            )

            Assert.False(File.Exists marker)
        finally
            DirectCommandProcess.delete directory
