namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open Dotnet.WorkspaceExplorer.Workspaces
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
            Assert.Equal(expected, conflictExpected)
            Assert.Equal(actual, conflictActual)
            Assert.Equal("workspace_conflict", WorkspaceErrorCode.WorkspaceConflict.Value)
            Assert.Equal("workspace.test", diagnostic.Code.Value)
        | outcome -> failwithf "Expected a typed conflict, got %A" outcome
