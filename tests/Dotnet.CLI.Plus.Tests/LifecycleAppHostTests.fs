namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System
open System.IO
open Dotnet.CLI.Plus.Transport
open FsUnit.Xunit
open Xunit

type LifecycleAppHostTests() =
    [<Fact>]
    member _.``should pass each pipe lifecycle mapping to one ordinary dotnet child``() =
        let capture =
            Path.Combine(
                BrokerProcess.root,
                ".agent-workspace",
                "mtp",
                $"lifecycle-{Guid.NewGuid():N}.jsonl"
            )

        Directory.CreateDirectory(Path.GetDirectoryName capture) |> ignore

        let session =
            CanonicalAppHost.startWithEnvironment
                "lifecycle-pipe"
                [ "DOTNET_PLUS_FAKE_HOST_CAPTURE", capture ]
                (fun directory model ->
                    File.WriteAllText(Path.Combine(directory, "App.csproj"), "<Project />")
                    model.AddProject("App.csproj", "App", null) |> ignore)

        try
            let empty = CanonicalAppHost.argumentMap []

            let restore =
                CanonicalAppHost.executeRead
                    session
                    3u
                    "lifecycle.restore"
                    session.ProjectId
                    empty
                    0L

            let build =
                CanonicalAppHost.executeRead
                    session
                    4u
                    "lifecycle.build"
                    None
                    (CanonicalAppHost.argumentMap
                        [ "noRestore", RpcValue.Boolean true
                          "arguments",
                          RpcValue.array [ RpcValue.String "--verbosity"; RpcValue.String "quiet" ] ])
                    0L

            let workspaceTest =
                CanonicalAppHost.executeRead
                    session
                    5u
                    "lifecycle.test"
                    None
                    (CanonicalAppHost.argumentMap
                        [ "arguments",
                          RpcValue.array
                              [ RpcValue.String "--no-restore"
                                RpcValue.String "--filter"
                                RpcValue.String "Category=Fast"
                                RpcValue.String "--logger"
                                RpcValue.String String.Empty ] ])
                    0L

            let projectTest =
                CanonicalAppHost.executeRead
                    session
                    6u
                    "lifecycle.test"
                    session.ProjectId
                    (CanonicalAppHost.argumentMap
                        [ "arguments",
                          RpcValue.array
                              [ RpcValue.String "--logger"
                                RpcValue.String "console"
                                RpcValue.String "--logger"
                                RpcValue.String "console" ] ])
                    0L

            let run =
                CanonicalAppHost.executeRead session 7u "lifecycle.run" session.ProjectId empty 0L

            restore.Outcome |> should equal "succeeded"
            build.Outcome |> should equal "succeeded"
            workspaceTest.Outcome |> should equal "succeeded"
            projectTest.Outcome |> should equal "succeeded"
            run.Outcome |> should equal "succeeded"

            CanonicalAppHost.captured capture
            |> should
                equal
                [| [| "restore"; Path.Combine(session.Directory, "App.csproj") |]
                   [| "build"; session.Solution; "--no-restore"; "--verbosity"; "quiet" |]
                   [| "test"
                      session.Solution
                      "--no-restore"
                      "--filter"
                      "Category=Fast"
                      "--logger"
                      String.Empty |]
                   [| "test"
                      Path.Combine(session.Directory, "App.csproj")
                      "--logger"
                      "console"
                      "--logger"
                      "console" |]
                   [| "run"; "--project"; Path.Combine(session.Directory, "App.csproj") |] |]
        finally
            CanonicalAppHost.stop session

            if File.Exists capture then
                File.Delete capture
