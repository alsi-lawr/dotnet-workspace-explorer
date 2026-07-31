namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.IO
open FsUnit.Xunit
open Xunit

[<Collection("Delegated dotnet processes")>]
type DotnetArgumentForwardingTests() =
    [<Fact>]
    member _.``package arguments including empty values and operands reach the delegated dotnet child unchanged``
        ()
        =
        let directory =
            DirectCommandProcess.temporaryDirectory "direct command-package-arguments"

        try
            let project = Path.Combine(directory, "App.fsproj")
            File.WriteAllText(project, "<Project />")

            let arguments =
                [ "package"
                  "add"
                  "Example.Package"
                  "--project"
                  project
                  "--extension-option"
                  String.Empty
                  "extension-operand" ]

            let result = DirectCommandProcess.run directory "capture" ("--json" :: arguments) []
            DirectCommandProcess.success result |> should equal true

            DirectCommandProcess.childArguments result
            |> should equal (List.toArray arguments)
        finally
            DirectCommandProcess.delete directory

    [<Fact>]
    member _.``restore, build, test, and run lifecycle arguments reach one delegated dotnet child unchanged``
        ()
        =
        let directory =
            DirectCommandProcess.temporaryDirectory "direct command-lifecycle-arguments"

        try
            for arguments in
                [ [ "restore"; "Demo.slnx" ], [| "restore"; "Demo.slnx" |]
                  [ "build"; "--no-restore"; "--verbosity"; "quiet" ],
                  [| "build"; "--no-restore"; "--verbosity"; "quiet" |]
                  [ "test"; "App.fsproj"; "--filter"; "Category=Fast" ],
                  [| "test"; "App.fsproj"; "--filter"; "Category=Fast" |]
                  [ "run"; "--project"; "App.fsproj" ], [| "run"; "--project"; "App.fsproj" |] ] do
                let supplied, expected = arguments
                let result = DirectCommandProcess.run directory "capture" ("--json" :: supplied) []
                DirectCommandProcess.success result |> should equal true
                DirectCommandProcess.childArguments result |> should equal expected
        finally
            DirectCommandProcess.delete directory

    [<Fact>]
    member _.``package, reference, template, and output commands succeed with expected mutation postconditions``
        ()
        =
        let directory =
            DirectCommandProcess.temporaryDirectory "direct command-postconditions"

        try
            let project = Path.Combine(directory, "App.fsproj")
            let reference = Path.Combine(directory, "Other.fsproj")
            let source = Path.Combine(directory, "app.cs")
            File.WriteAllText(reference, "<Project />")

            File.WriteAllText(
                project,
                "<Project><ItemGroup>"
                + "<PackageReference Include=\"Example.Package\" Version=\"2.0.0\" />"
                + "<ProjectReference Include=\"Other.fsproj\" />"
                + "</ItemGroup></Project>"
            )

            File.WriteAllText(source, "#:package Example.Package@2.0.0\nConsole.WriteLine(1);")

            let home = Path.Combine(directory, "home")

            let cache =
                Path.Combine(home, ".templateengine", "dotnetcli", "test", "templatecache.json")

            Directory.CreateDirectory(Path.GetDirectoryName cache) |> ignore
            File.WriteAllText(cache, "{\"MountPointsInfo\":{\"Example.Template\":{}}}")

            let cases =
                [ "capture",
                  [ "package"
                    "add"
                    "Example.Package"
                    "--version"
                    "2.0.0"
                    "--project"
                    project ],
                  []
                  "capture", [ "reference"; "add"; reference; "--project"; project ], []
                  "capture", [ "package"; "add"; "Example.Package@2.0.0"; "--file"; source ], []
                  "capture", [ "new"; "install"; "Example.Template" ], [ "DOTNET_CLI_HOME", home ]
                  "create-output",
                  [ "new"; "console"; "--output"; Path.Combine(directory, "created") ],
                  [] ]

            for mode, arguments, environment in cases do
                let result =
                    DirectCommandProcess.run directory mode ("--json" :: arguments) environment

                (DirectCommandProcess.success result) |> should equal true
        finally
            DirectCommandProcess.delete directory
