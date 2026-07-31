namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open Dotnet.WorkspaceExplorer.Workspaces
open FsUnit.Xunit
open Xunit

[<Collection("Core contracts")>]
type WorkspaceConflictTests() =
    [<Fact>]
    member _.``should preserve both revisions and the stable failure code for conflicts``() =
        let expected = WorkspaceRevision.Create 5L
        let actual = WorkspaceRevision.Create 6L

        match
            WorkspaceRevisionPrecondition.Check(
                expected,
                actual,
                WorkspaceContractScenario.diagnostic ()
            )
        with
        | Failure(Conflict(conflictExpected, conflictActual, diagnostic)) ->
            (conflictExpected) |> should equal (expected)
            (conflictActual) |> should equal (actual)

            (WorkspaceErrorCode.WorkspaceConflict.Value)
            |> should equal ("workspace_conflict")

            (diagnostic.Code.Value) |> should equal ("workspace.test")
        | outcome -> failwithf "Expected a typed conflict, got %A" outcome
