namespace Dotnet.WorkspaceExplorer.PackageExplorer.UnitTests

open System
open System.IO
open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Packages
open FsUnit.Xunit
open Xunit

module private PackageStartupScenario =
    let temporaryDirectory () =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-we-package-startup-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        path

    let delete path =
        if Directory.Exists path then
            Directory.Delete(path, true)

    let file directory name =
        let path = Path.Combine(directory, name)
        File.WriteAllText(path, "")
        path

    let terminalPath currentDirectory arguments =
        match PackageStartup.resolve currentDirectory arguments with
        | PackageStartup.Terminal target ->
            PackageWorkspaceTarget.path target, PackageWorkspaceTarget.kind target
        | startup -> failwithf "Expected terminal startup, got %A" startup

[<Sealed>]
type PackageStartupTests() =
    [<Fact>]
    member _.``packages with an omitted target resolves the single workspace in the current directory``
        ()
        =
        let directory = PackageStartupScenario.temporaryDirectory ()

        try
            let solution = PackageStartupScenario.file directory "Example.slnx"
            let path, kind = PackageStartupScenario.terminalPath directory [| "packages" |]
            path |> should equal (Path.GetFullPath solution)
            kind |> should equal PackageWorkspaceTargetKind.SolutionXml
        finally
            PackageStartupScenario.delete directory

    [<Fact>]
    member _.``packages with an omitted target returns a stable missing-target diagnostic``() =
        let directory = PackageStartupScenario.temporaryDirectory ()

        try
            match PackageStartup.resolve directory [| "packages" |] with
            | PackageStartup.Invalid failure ->
                failure.Code |> should equal "DWE-PACKAGES-TARGET-NOT-FOUND"
                failure.Message |> should haveSubstring (Path.GetFullPath directory)
            | startup -> failwithf "Expected missing target, got %A" startup
        finally
            PackageStartupScenario.delete directory

    [<Fact>]
    member _.``packages with an omitted target returns sorted candidates in a stable ambiguous diagnostic``
        ()
        =
        let directory = PackageStartupScenario.temporaryDirectory ()

        try
            let project = PackageStartupScenario.file directory "Zeta.csproj"
            let solution = PackageStartupScenario.file directory "Alpha.sln"

            match PackageStartup.resolve directory [| "packages" |] with
            | PackageStartup.Invalid(PackageStartupFailure.AmbiguousWorkspace(_, candidates)) ->
                candidates
                |> NonEmptyList.toList
                |> should equal [ Path.GetFullPath solution; Path.GetFullPath project ]
            | startup -> failwithf "Expected ambiguous target, got %A" startup
        finally
            PackageStartupScenario.delete directory

    [<Fact>]
    member _.``packages accepts explicit solution solution-xml and solution-filter targets``() =
        let directory = PackageStartupScenario.temporaryDirectory ()

        try
            for name, expected in
                [ "Example.sln", PackageWorkspaceTargetKind.Solution
                  "Example.slnx", PackageWorkspaceTargetKind.SolutionXml
                  "Example.slnf", PackageWorkspaceTargetKind.SolutionFilter ] do
                let target = PackageStartupScenario.file directory name

                let path, kind =
                    PackageStartupScenario.terminalPath directory [| "packages"; target |]

                path |> should equal (Path.GetFullPath target)
                kind |> should equal expected
        finally
            PackageStartupScenario.delete directory

    [<Fact>]
    member _.``packages accepts explicit CSharp FSharp and VisualBasic project targets``() =
        let directory = PackageStartupScenario.temporaryDirectory ()

        try
            for name, expected in
                [ "Example.csproj", PackageProjectLanguage.CSharp
                  "Example.fsproj", PackageProjectLanguage.FSharp
                  "Example.vbproj", PackageProjectLanguage.VisualBasic ] do
                let target = PackageStartupScenario.file directory name

                let path, kind =
                    PackageStartupScenario.terminalPath directory [| "packages"; target |]

                path |> should equal (Path.GetFullPath target)
                kind |> should equal (PackageWorkspaceTargetKind.Project expected)
        finally
            PackageStartupScenario.delete directory

    [<Fact>]
    member _.``packages resolves an explicit relative target from the supplied current directory``
        ()
        =
        let directory = PackageStartupScenario.temporaryDirectory ()

        try
            let target = PackageStartupScenario.file directory "Example.csproj"

            let path, kind =
                PackageStartupScenario.terminalPath directory [| "packages"; "Example.csproj" |]

            path |> should equal (Path.GetFullPath target)

            kind
            |> should equal (PackageWorkspaceTargetKind.Project PackageProjectLanguage.CSharp)
        finally
            PackageStartupScenario.delete directory

    [<Fact>]
    member _.``packages accepts an explicit workspace directory without guessing a nested target``
        ()
        =
        let directory = PackageStartupScenario.temporaryDirectory ()

        try
            PackageStartupScenario.file directory "One.sln" |> ignore
            PackageStartupScenario.file directory "Two.sln" |> ignore

            let path, kind =
                PackageStartupScenario.terminalPath directory [| "packages"; directory |]

            path |> should equal (Path.GetFullPath directory)
            kind |> should equal PackageWorkspaceTargetKind.Directory
        finally
            PackageStartupScenario.delete directory

    [<Fact>]
    member _.``packages rejects explicit missing and unsupported targets with stable diagnostics``
        ()
        =
        let directory = PackageStartupScenario.temporaryDirectory ()

        try
            let missing = Path.Combine(directory, "Missing.csproj")
            let unsupported = PackageStartupScenario.file directory "README.md"

            match PackageStartup.resolve directory [| "packages"; missing |] with
            | PackageStartup.Invalid failure ->
                failure.Code |> should equal "DWE-PACKAGES-TARGET-NOT-FOUND"
            | startup -> failwithf "Expected missing target, got %A" startup

            match PackageStartup.resolve directory [| "packages"; unsupported |] with
            | PackageStartup.Invalid failure ->
                failure.Code |> should equal "DWE-PACKAGES-UNSUPPORTED-TARGET"
            | startup -> failwithf "Expected unsupported target, got %A" startup
        finally
            PackageStartupScenario.delete directory

    [<Fact>]
    member _.``packages target pipe selects only the package RPC startup``() =
        let directory = PackageStartupScenario.temporaryDirectory ()

        try
            let target = PackageStartupScenario.file directory "Example.fsproj"

            match PackageStartup.resolve directory [| "packages"; target; "--pipe" |] with
            | PackageStartup.Pipe selected ->
                PackageWorkspaceTarget.path selected |> should equal (Path.GetFullPath target)
            | startup -> failwithf "Expected package pipe startup, got %A" startup
        finally
            PackageStartupScenario.delete directory

    [<Fact>]
    member _.``packages rejects omitted duplicate misplaced and extra pipe startup tokens``() =
        let directory = PackageStartupScenario.temporaryDirectory ()

        try
            let target = PackageStartupScenario.file directory "Example.csproj"

            for arguments in
                [ [| "packages"; "--pipe" |]
                  [| "packages"; "--pipe"; target |]
                  [| "packages"; target; "--pipe"; "--pipe" |]
                  [| "packages"; target; "extra"; "--pipe" |] ] do
                match PackageStartup.resolve directory arguments with
                | PackageStartup.Invalid failure ->
                    failure.Code |> should equal "DWE-PACKAGES-INVALID-INVOCATION"
                | startup ->
                    failwithf "Expected invalid invocation for %A, got %A" arguments startup
        finally
            PackageStartupScenario.delete directory

    [<Fact>]
    member _.``packages rejects public package command wrappers before resolving them as paths``() =
        let directory = PackageStartupScenario.temporaryDirectory ()

        try
            for command in [ "add"; "remove"; "update"; "list"; "search" ] do
                match PackageStartup.resolve directory [| "packages"; command |] with
                | PackageStartup.Invalid failure ->
                    failure.Code |> should equal "DWE-PACKAGES-INVALID-INVOCATION"
                    failure.Message |> should haveSubstring "standard dotnet package"
                | startup -> failwithf "Expected rejected command %s, got %A" command startup
        finally
            PackageStartupScenario.delete directory
