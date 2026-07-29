namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open Xunit

[<CollectionDefinition("Core contracts")>]
type CorecontractsCollection() = class end

[<CollectionDefinition("Workspace edits")>]
type WorkspaceeditsCollection() = class end

[<CollectionDefinition("Solution contracts")>]
type SolutioncontractsCollection() = class end

[<CollectionDefinition("Solution edits")>]
type SolutioneditsCollection() = class end
