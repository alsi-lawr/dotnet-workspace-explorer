namespace Dotnet.WorkspaceExplorer.PackageExplorer.UnitTests

open System
open System.IO
open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Packages
open FsUnit.Xunit
open Xunit

module private PackageStartupScenario =
    let temporaryProject () =
        let directory =
            Path.Combine(Path.GetTempPath(), $"dotnet-we-package-startup-{Guid.NewGuid():N}")

        Directory.CreateDirectory directory |> ignore
        let project = Path.Combine(directory, "Example.fsproj")
        File.WriteAllText(project, "")
        directory, project

[<Sealed>]
type PackageStartupTests() =
    [<Fact>]
    member _.``package startup accepts an explicit target only for the package RPC pipe``() =
        let directory, project = PackageStartupScenario.temporaryProject ()

        try
            match PackageStartup.resolve directory [| "packages"; project; "--pipe" |] with
            | PackageStartup.Pipe target ->
                PackageWorkspaceTarget.path target |> should equal (Path.GetFullPath project)
            | startup -> failwithf "Expected package pipe startup, got %A" startup
        finally
            Directory.Delete(directory, true)

    [<Theory>]
    [<InlineData("packages")>]
    [<InlineData("packages Example.fsproj")>]
    [<InlineData("packages --pipe")>]
    member _.``package startup rejects every no-pipe or targetless in-process product route``
        (commandLine: string)
        =
        let directory, _ = PackageStartupScenario.temporaryProject ()

        try
            let arguments = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)

            match PackageStartup.resolve directory arguments with
            | PackageStartup.Invalid failure ->
                failure.Code |> should equal "DWE-PACKAGES-INVALID-INVOCATION"
                failure.Message |> should haveSubstring "packages <TARGET> --pipe"
            | startup -> failwithf "Expected invalid package startup, got %A" startup
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``package startup rejects direct package command wrappers``() =
        let directory, _ = PackageStartupScenario.temporaryProject ()

        try
            for command in [ "add"; "remove"; "update"; "list"; "search" ] do
                match PackageStartup.resolve directory [| "packages"; command |] with
                | PackageStartup.Invalid failure ->
                    failure.Code |> should equal "DWE-PACKAGES-INVALID-INVOCATION"
                    failure.Message |> should haveSubstring "standard dotnet package"
                | startup -> failwithf "Expected rejected command %s, got %A" command startup
        finally
            Directory.Delete(directory, true)
