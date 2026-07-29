namespace Dotnet.WorkspaceExplorer.Solutions

open Dotnet.WorkspaceExplorer.Workspaces

#nowarn "3261"
#nowarn "3262"


type PlannedSolutionEdit =
    { Request: WorkspaceEditPreviewRequest
      Contents: byte array
      BackingPath: WorkspaceArtifactPath
      FileRename: PlannedProjectFileRename option }

and PlannedProjectFileRename =
    { Source: WorkspaceArtifactPath
      Destination: WorkspaceArtifactPath }
