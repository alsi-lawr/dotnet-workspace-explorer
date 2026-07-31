namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open Dotnet.WorkspaceExplorer.Workspaces
open FsUnit.Xunit
open Xunit

[<Collection("Core contracts")>]
type WorkspaceConflictTests() =
    [<Fact>]
    member _.``a revision precondition conflict preserves both revisions, the stable error code, and its diagnostic``
        ()
        =
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
