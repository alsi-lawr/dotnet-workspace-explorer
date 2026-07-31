namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System.IO
open System.Text.Json
open FsUnit.Xunit
open Xunit

[<Collection("Launch-profile scenarios")>]
type LaunchProfileStorageTests() =
    [<Fact>]
    member _.``setting an ordered launch profile for sln and slnx stores project actions, lists the profile, and removing it empties the file``
        ()
        =
        for extension in [ ".sln"; ".slnx" ] do
            let directory = DirectCommandProcess.temporaryDirectory $"launch-profile{extension}"

            try
                let solution, first, second =
                    LaunchProfileScenario.createSolution directory extension

                let set =
                    LaunchProfileScenario.run
                        directory
                        [ "solution"; solution; "launch"; "set"; "Both"; second; first ]

                DirectCommandProcess.success set |> should equal true
                DirectCommandProcess.childArguments set |> should equal [||]

                let profile = Path.ChangeExtension(solution, ".slnLaunch")
                use document = JsonDocument.Parse(File.ReadAllText profile)
                let projects = document.RootElement[0].GetProperty "Projects"
                projects[0].GetProperty("Path").GetString() |> should equal "Second.fsproj"
                projects[1].GetProperty("Path").GetString() |> should equal "First.fsproj"

                projects[0].GetProperty("Action").GetString()
                |> should equal "StartWithoutDebugging"

                let listed =
                    LaunchProfileScenario.run directory [ "sln"; solution; "launch"; "list" ]

                DirectCommandProcess.success listed |> should equal true
                LaunchProfileScenario.output listed |> should equal "Both\n"

                let removed =
                    LaunchProfileScenario.run
                        directory
                        [ "solution"; solution; "launch"; "remove"; "Both" ]

                DirectCommandProcess.success removed |> should equal true
                File.ReadAllText(profile).Trim() |> should equal "[]"
            finally
                DirectCommandProcess.delete directory

    [<Fact>]
    member _.``updating selected projects preserves unknown launch profile and project fields in the launch profile``
        ()
        =
        let directory =
            DirectCommandProcess.temporaryDirectory "launch-profile-unknown-fields"

        try
            let solution, _, _ = LaunchProfileScenario.createSolution directory ".slnx"
            let nested = Path.Combine(directory, "Nested")
            Directory.CreateDirectory nested |> ignore
            let project = Path.Combine(nested, "App.fsproj")
            File.WriteAllText(project, "<Project />")
            DirectCommandProcess.saveSolution solution [ project ]
            let profile = Path.ChangeExtension(solution, ".slnLaunch")

            File.WriteAllText(
                profile,
                "[{\"Name\":\"Start\",\"Unknown\":{\"nested\":true},\"Projects\":[{\"Path\":\"Nested\\\\App.fsproj\",\"Action\":\"Start\",\"Keep\":\"yes\"}]}]"
            )

            let updated =
                LaunchProfileScenario.run
                    directory
                    [ "solution"; solution; "launch"; "set"; "Start"; project ]

            DirectCommandProcess.success updated |> should equal true
            use document = JsonDocument.Parse(File.ReadAllText profile)

            document.RootElement[0].GetProperty("Unknown").GetProperty("nested").GetBoolean()
            |> should equal true

            let projects = document.RootElement[0].GetProperty "Projects"
            projects[0].GetProperty("Keep").GetString() |> should equal "yes"
        finally
            DirectCommandProcess.delete directory

    [<Fact>]
    member _.``malformed or duplicate launch profiles are rejected without rewriting the launch profile file``
        ()
        =
        let inputs =
            [ "[", "["
              "[{\"Name\":\"Same\",\"Projects\":[]},{\"Name\":\"Same\",\"Projects\":[]}]",
              "[{\"Name\":\"Same\",\"Projects\":[]},{\"Name\":\"Same\",\"Projects\":[]}]" ]

        for contents, expected in inputs do
            let directory = DirectCommandProcess.temporaryDirectory "launch-profile-invalid"

            try
                let solution, _, project = LaunchProfileScenario.createSolution directory ".slnx"
                let profile = Path.ChangeExtension(solution, ".slnLaunch")
                File.WriteAllText(profile, contents)

                let result =
                    LaunchProfileScenario.run
                        directory
                        [ "solution"; solution; "launch"; "set"; "Start"; project ]

                DirectCommandProcess.success result |> should equal false
                DirectCommandProcess.diagnosticCode result |> should equal "invalid_input"
                File.ReadAllText profile |> should equal expected
            finally
                DirectCommandProcess.delete directory
