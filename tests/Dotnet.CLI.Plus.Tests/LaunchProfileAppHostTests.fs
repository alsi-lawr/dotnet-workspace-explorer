namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System.IO
open System.Text.Json
open FsUnit.Xunit
open Dotnet.CLI.Plus.Transport
open Xunit

module private LaunchProfileAppHost =
    let run directory arguments =
        BrokerProcess.run directory "capture" ("--json" :: arguments) []

    let createSolution directory extension =
        let first = Path.Combine(directory, "First.fsproj")
        let second = Path.Combine(directory, "Second.fsproj")
        let solution = Path.Combine(directory, $"Demo{extension}")
        File.WriteAllText(first, "<Project />")
        File.WriteAllText(second, "<Project />")
        BrokerProcess.saveSolution solution [ first; second ]
        solution, first, second

    let output result =
        use document = BrokerProcess.json result
        document.RootElement.GetProperty("result").GetProperty("standardOutput").GetString()

type LaunchProfileAppHostTests() =
    [<Fact>]
    member _.``should store ordered launch data for sln and slnx without launching projects``() =
        for extension in [ ".sln"; ".slnx" ] do
            let directory = BrokerProcess.temporaryDirectory $"launch-profile{extension}"

            try
                let solution, first, second =
                    LaunchProfileAppHost.createSolution directory extension

                let set =
                    LaunchProfileAppHost.run
                        directory
                        [ "solution"; solution; "launch"; "set"; "Both"; second; first ]

                BrokerProcess.success set |> should equal true
                BrokerProcess.childArguments set |> should equal [||]

                let profile = Path.ChangeExtension(solution, ".slnLaunch")
                use document = JsonDocument.Parse(File.ReadAllText profile)
                let projects = document.RootElement[0].GetProperty "Projects"
                projects[0].GetProperty("Path").GetString() |> should equal "Second.fsproj"
                projects[1].GetProperty("Path").GetString() |> should equal "First.fsproj"

                projects[0].GetProperty("Action").GetString()
                |> should equal "StartWithoutDebugging"

                let listed =
                    LaunchProfileAppHost.run directory [ "sln"; solution; "launch"; "list" ]

                BrokerProcess.success listed |> should equal true
                LaunchProfileAppHost.output listed |> should equal "Both\n"

                let removed =
                    LaunchProfileAppHost.run
                        directory
                        [ "solution"; solution; "launch"; "remove"; "Both" ]

                BrokerProcess.success removed |> should equal true
                File.ReadAllText(profile).Trim() |> should equal "[]"
            finally
                BrokerProcess.delete directory

    [<Fact>]
    member _.``should preserve unknown launch profile fields when updating selected projects``() =
        let directory = BrokerProcess.temporaryDirectory "launch-profile-unknown-fields"

        try
            let solution, _, _ = LaunchProfileAppHost.createSolution directory ".slnx"
            let nested = Path.Combine(directory, "Nested")
            Directory.CreateDirectory nested |> ignore
            let project = Path.Combine(nested, "App.fsproj")
            File.WriteAllText(project, "<Project />")
            BrokerProcess.saveSolution solution [ project ]
            let profile = Path.ChangeExtension(solution, ".slnLaunch")

            File.WriteAllText(
                profile,
                "[{\"Name\":\"Start\",\"Unknown\":{\"nested\":true},\"Projects\":[{\"Path\":\"Nested\\\\App.fsproj\",\"Action\":\"Start\",\"Keep\":\"yes\"}]}]"
            )

            let updated =
                LaunchProfileAppHost.run
                    directory
                    [ "solution"; solution; "launch"; "set"; "Start"; project ]

            BrokerProcess.success updated |> should equal true
            use document = JsonDocument.Parse(File.ReadAllText profile)

            document.RootElement[0].GetProperty("Unknown").GetProperty("nested").GetBoolean()
            |> should equal true

            let projects = document.RootElement[0].GetProperty "Projects"
            projects[0].GetProperty("Keep").GetString() |> should equal "yes"
        finally
            BrokerProcess.delete directory

    [<Fact>]
    member _.``should refuse malformed and duplicate launch profiles without rewriting them``() =
        let inputs =
            [ "[", "["
              "[{\"Name\":\"Same\",\"Projects\":[]},{\"Name\":\"Same\",\"Projects\":[]}]",
              "[{\"Name\":\"Same\",\"Projects\":[]},{\"Name\":\"Same\",\"Projects\":[]}]" ]

        for contents, expected in inputs do
            let directory = BrokerProcess.temporaryDirectory "launch-profile-invalid"

            try
                let solution, _, project = LaunchProfileAppHost.createSolution directory ".slnx"
                let profile = Path.ChangeExtension(solution, ".slnLaunch")
                File.WriteAllText(profile, contents)

                let result =
                    LaunchProfileAppHost.run
                        directory
                        [ "solution"; solution; "launch"; "set"; "Start"; project ]

                BrokerProcess.success result |> should equal false
                BrokerProcess.diagnosticCode result |> should equal "invalid_input"
                File.ReadAllText profile |> should equal expected
            finally
                BrokerProcess.delete directory

    [<Fact>]
    member _.``should list the backing launch profile for slnf and refuse its edits``() =
        let directory = BrokerProcess.temporaryDirectory "launch-profile-filter"

        try
            let backing, project, _ = LaunchProfileAppHost.createSolution directory ".slnx"
            let profile = Path.ChangeExtension(backing, ".slnLaunch")
            let filter = Path.Combine(directory, "Filtered.slnf")
            File.WriteAllText(profile, "[{\"Name\":\"Start\",\"Projects\":[]}]")

            File.WriteAllText(
                filter,
                $"{{\"solution\":{{\"path\":\"{Path.GetFileName backing}\",\"projects\":[\"{Path.GetFileName project}\"]}}}}"
            )

            let listed =
                LaunchProfileAppHost.run directory [ "solution"; filter; "launch"; "list" ]

            BrokerProcess.success listed |> should equal true
            LaunchProfileAppHost.output listed |> should equal "Start\n"

            let edit =
                LaunchProfileAppHost.run
                    directory
                    [ "solution"; filter; "launch"; "set"; "Start"; project ]

            BrokerProcess.success edit |> should equal false

            File.ReadAllText profile
            |> should equal "[{\"Name\":\"Start\",\"Projects\":[]}]"
        finally
            BrokerProcess.delete directory

    [<Fact>]
    member _.``should publish a pipe launch-profile mutation after preview and verification``() =
        let session =
            CanonicalAppHost.start "launch-profile-pipe" (fun directory model ->
                File.WriteAllText(Path.Combine(directory, "App.csproj"), "<Project />")
                model.AddProject("App.csproj", "App", null) |> ignore)

        try
            let completion =
                CanonicalAppHost.execute
                    session
                    3u
                    "solution.launch.set"
                    None
                    (CanonicalAppHost.argumentMap
                        [ "name", RpcValue.String "Start"
                          "projects",
                          RpcValue.array
                              [ RpcValue.String(Path.Combine(session.Directory, "App.csproj")) ] ])
                    0L

            completion.Outcome |> should equal "succeeded"

            completion.Notifications
            |> should equal [ "operation/progress"; "operation/completed" ]

            let profile = Path.ChangeExtension(session.Solution, ".slnLaunch")
            use document = JsonDocument.Parse(File.ReadAllText profile)
            let project = document.RootElement[0].GetProperty("Projects")[0]
            project.GetProperty("Path").GetString() |> should equal "App.csproj"

            project.GetProperty("Action").GetString()
            |> should equal "StartWithoutDebugging"

            let listed =
                CanonicalAppHost.executeRead
                    session
                    5u
                    "solution.launch.list"
                    None
                    (CanonicalAppHost.argumentMap [])
                    completion.Revision

            listed.Output |> should equal [ "Start\n" ]

            listed.Notifications
            |> should equal [ "operation/progress"; "operation/output"; "operation/completed" ]

            let removed =
                CanonicalAppHost.execute
                    session
                    7u
                    "solution.launch.remove"
                    None
                    (CanonicalAppHost.argumentMap [ "name", RpcValue.String "Start" ])
                    listed.Revision

            removed.Outcome |> should equal "succeeded"

            removed.Notifications
            |> should equal [ "operation/progress"; "operation/completed" ]

            File.ReadAllText(profile).Trim() |> should equal "[]"
        finally
            CanonicalAppHost.stop session
