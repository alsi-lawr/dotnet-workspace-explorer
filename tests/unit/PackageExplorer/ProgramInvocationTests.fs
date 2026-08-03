namespace Dotnet.WorkspaceExplorer.PackageExplorer.UnitTests

open System
open System.IO
open Dotnet.WorkspaceExplorer
open Dotnet.WorkspaceExplorer.Packages
open FsUnit.Xunit
open Xunit

module private ProgramInvocationScenario =
    let temporaryProject () =
        let directory =
            Path.Combine(Path.GetTempPath(), $"dotnet-we-program-invocation-{Guid.NewGuid():N}")

        Directory.CreateDirectory directory |> ignore
        let project = Path.Combine(directory, "Example.vbproj")
        File.WriteAllText(project, "")
        directory, project

[<Sealed>]
type ProgramInvocationTests() =
    [<Fact>]
    member _.``program invocation gives the internal project evaluation host final precedence``() =
        let arguments = [| "internal"; "project-evaluation-host"; "--sdk"; "/sdk" |]

        ProgramInvocation.parse "/workspace" arguments
        |> should equal (ProgramInvocation.ProjectEvaluationHost "/sdk")

    [<Fact>]
    member _.``program invocation gives package terminal and pipe startup distinct final routes``
        ()
        =
        let directory, project = ProgramInvocationScenario.temporaryProject ()

        try
            match ProgramInvocation.parse directory [| "packages"; project |] with
            | ProgramInvocation.PackageTerminal target ->
                PackageWorkspaceTarget.path target |> should equal (Path.GetFullPath project)
            | route -> failwithf "Expected package terminal route, got %A" route

            match ProgramInvocation.parse directory [| "packages"; project; "--pipe" |] with
            | ProgramInvocation.PackagePipe target ->
                PackageWorkspaceTarget.path target |> should equal (Path.GetFullPath project)
            | route -> failwithf "Expected package pipe route, got %A" route
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    member _.``program invocation preserves workspace pipe and direct solution arguments unchanged``
        ()
        =
        let workspaceArguments = [| "workspace"; "Example.slnx"; "--pipe" |]
        let directArguments = [| "solution"; "Example.slnx"; "launch"; "list" |]

        match ProgramInvocation.parse "/workspace" workspaceArguments with
        | ProgramInvocation.ExistingRoute arguments -> arguments |> should equal workspaceArguments
        | route -> failwithf "Expected existing workspace route, got %A" route

        match ProgramInvocation.parse "/workspace" directArguments with
        | ProgramInvocation.ExistingRoute arguments -> arguments |> should equal directArguments
        | route -> failwithf "Expected existing direct route, got %A" route
