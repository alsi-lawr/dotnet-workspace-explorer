namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System.IO
open FsUnit.Xunit
open Xunit

[<Collection("Delegated dotnet processes")>]
type DelegatedEditBoundaryTests() =
    [<Fact>]
    member _.``should verify package versions and references in the requested framework condition``
        ()
        =
        let directory =
            DirectCommandProcess.temporaryDirectory "direct command-framework-postconditions"

        try
            let project = Path.Combine(directory, "App.fsproj")
            let wrongReference = Path.Combine(directory, "Wrong.fsproj")
            let requestedReference = Path.Combine(directory, "Requested.fsproj")
            let net9Condition = "'$(TargetFramework)' == 'net9.0'"
            let net10Condition = "'$(TargetFramework)' == 'net10.0'"

            File.WriteAllText(wrongReference, "<Project />")
            File.WriteAllText(requestedReference, "<Project />")

            File.WriteAllText(
                project,
                "<Project>"
                + $"<ItemGroup Condition=\"{net9Condition}\">"
                + "<PackageReference Include=\"Example.Package\" />"
                + "<ProjectReference Include=\"Wrong.fsproj\" />"
                + "</ItemGroup>"
                + $"<ItemGroup Condition=\"{net10Condition}\">"
                + "<PackageReference Include=\"example.package\" />"
                + "<ProjectReference Include=\"Requested.fsproj\" />"
                + "</ItemGroup>"
                + "</Project>"
            )

            File.WriteAllText(
                Path.Combine(directory, "Directory.Packages.props"),
                "<Project>"
                + $"<ItemGroup Condition=\"{net9Condition}\">"
                + "<PackageVersion Include=\"EXAMPLE.PACKAGE\" Version=\"9.0.0\" />"
                + "</ItemGroup>"
                + $"<ItemGroup Condition=\"{net10Condition}\">"
                + "<PackageVersion Include=\"EXAMPLE.PACKAGE\" Version=\"10.0.0\" />"
                + "</ItemGroup>"
                + "</Project>"
            )

            let package version =
                DirectCommandProcess.run
                    directory
                    "capture"
                    [ "--json"
                      "package"
                      "add"
                      "Example.Package"
                      "--version"
                      version
                      "--project"
                      project
                      "--framework"
                      "net10.0" ]
                    []

            DirectCommandProcess.success (package "10.0.0") |> should equal true
            DirectCommandProcess.success (package "9.0.0") |> should equal false

            let reference path =
                DirectCommandProcess.run
                    directory
                    "capture"
                    [ "--json"
                      "reference"
                      "add"
                      path
                      "--project"
                      project
                      "--framework"
                      "net10.0" ]
                    []

            DirectCommandProcess.success (reference requestedReference) |> should equal true
            DirectCommandProcess.success (reference wrongReference) |> should equal false
        finally
            DirectCommandProcess.delete directory

    [<Fact>]
    member _.``should reject unsafe mutation targets before launching dotnet``() =
        let directory = DirectCommandProcess.temporaryDirectory "direct command-preflight"

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let marker = Path.Combine(directory, "launched")
            DirectCommandProcess.saveSolution solution []

            let cases =
                [ [ "package"; "add"; "Example.Package"; "--project"; solution ], "invalid_input"
                  [ "reference"; "add"; "Other.fsproj"; "--project"; solution ], "invalid_input"
                  [ "solution"; "read-only.slnf"; "add"; "App.fsproj" ], "unsupported_capability"
                  [ "solution"; solution; "add"; Path.Combine(directory, "none", "*.fsproj") ],
                  "invalid_input" ]

            for arguments, expectedCode in cases do
                let result =
                    DirectCommandProcess.run
                        directory
                        "marker"
                        ("--json" :: arguments)
                        [ "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_STARTED_PATH", marker ]

                Assert.False(DirectCommandProcess.success result)
                Assert.Equal(expectedCode, DirectCommandProcess.diagnosticCode result)
                Assert.False(File.Exists marker)
        finally
            DirectCommandProcess.delete directory

    [<Fact>]
    member _.``should verify solution membership with glob sentinel and filesystem case rules``() =
        let directory = DirectCommandProcess.temporaryDirectory "direct command-paths"

        try
            let project = Path.Combine(directory, "src", "Actual.fsproj")
            Directory.CreateDirectory(Path.GetDirectoryName project) |> ignore
            File.WriteAllText(project, "<Project />")
            let solution = Path.Combine(directory, "Demo.sln")
            DirectCommandProcess.saveSolution solution [ "src/Actual.fsproj" ]

            for operand in [ Path.Combine(directory, "**", "*.fsproj"); "--"; project ] do
                let arguments =
                    if operand = "--" then
                        [ "--json"; "solution"; solution; "add"; "--"; project ]
                    else
                        [ "--json"; "solution"; solution; "add"; operand ]

                Assert.True(
                    DirectCommandProcess.success (
                        DirectCommandProcess.run directory "capture" arguments []
                    )
                )

            let caseSemantics =
                Dotnet.WorkspaceExplorer.Workspaces.FileSystemCaseSensitivityDetector.DetectFromExistingPath
                    solution

            if
                caseSemantics = Dotnet.WorkspaceExplorer.Workspaces.FileSystemCaseSensitivity.Sensitive
            then
                let mismatched = Path.Combine(directory, "src", "actual.fsproj")

                let result =
                    DirectCommandProcess.run
                        directory
                        "capture"
                        [ "--json"; "solution"; solution; "add"; mismatched ]
                        []

                Assert.False(DirectCommandProcess.success result)
        finally
            DirectCommandProcess.delete directory
