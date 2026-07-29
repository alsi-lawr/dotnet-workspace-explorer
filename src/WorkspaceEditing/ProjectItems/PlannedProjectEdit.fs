namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open Dotnet.WorkspaceExplorer.Workspaces

type internal PlannedProjectEdit =
    { Request: WorkspaceEditPreviewRequest
      Actions: WorkspaceEditAction array
      Paths: WorkspaceArtifactPath array }
