namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System.IO
open FsUnit.Xunit
open Xunit

[<Collection("Launch-profile scenarios")>]
type SolutionFilterLaunchProfileTests() =
    [<Fact>]
    member _.``listing an slnf launch profile succeeds while launch profile edits are rejected without changing the backing file``
        ()
        =
        let directory = DirectCommandProcess.temporaryDirectory "launch-profile-filter"

        try
            let backing, project, _ = LaunchProfileScenario.createSolution directory ".slnx"
            let profile = Path.ChangeExtension(backing, ".slnLaunch")
            let filter = Path.Combine(directory, "Filtered.slnf")
            File.WriteAllText(profile, "[{\"Name\":\"Start\",\"Projects\":[]}]")

            File.WriteAllText(
                filter,
                $"{{\"solution\":{{\"path\":\"{Path.GetFileName backing}\",\"projects\":[\"{Path.GetFileName project}\"]}}}}"
            )

            let listed =
                LaunchProfileScenario.run directory [ "solution"; filter; "launch"; "list" ]

            DirectCommandProcess.success listed |> should equal true
            LaunchProfileScenario.output listed |> should equal "Start\n"

            let edit =
                LaunchProfileScenario.run
                    directory
                    [ "solution"; filter; "launch"; "set"; "Start"; project ]

            DirectCommandProcess.success edit |> should equal false

            File.ReadAllText profile
            |> should equal "[{\"Name\":\"Start\",\"Projects\":[]}]"
        finally
            DirectCommandProcess.delete directory
