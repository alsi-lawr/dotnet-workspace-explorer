namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Xml.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Dotnet.WorkspaceExplorer.WorkspaceCommands
open Dotnet.WorkspaceExplorer.CommandLine
open FsUnit.Xunit
open Xunit

[<Collection("Delegated dotnet processes")>]
type DotnetArgumentForwardingTests() =
    [<Fact>]
    member _.``should forward empty values and operands through child-owned package arguments``() =
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
    member _.``should delegate lifecycle arguments to one ordinary dotnet child``() =
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
    member _.``should verify package reference template file and output mutation results``() =
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

                Assert.True(DirectCommandProcess.success result, result.StandardOutput)
        finally
            DirectCommandProcess.delete directory
