namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System.IO

module private LaunchProfileScenario =
    let run directory arguments =
        DirectCommandProcess.run directory "capture" ("--json" :: arguments) []

    let createSolution directory extension =
        let first = Path.Combine(directory, "First.fsproj")
        let second = Path.Combine(directory, "Second.fsproj")
        let solution = Path.Combine(directory, $"Demo{extension}")
        File.WriteAllText(first, "<Project />")
        File.WriteAllText(second, "<Project />")
        DirectCommandProcess.saveSolution solution [ first; second ]
        solution, first, second

    let output result =
        use document = DirectCommandProcess.json result
        document.RootElement.GetProperty("result").GetProperty("output").GetString()
