namespace Dotnet.WorkspaceExplorer.Solutions

open Dotnet.WorkspaceExplorer.Workspaces

#nowarn "3261"
#nowarn "3262"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Workspaces
open Microsoft.VisualStudio.SolutionPersistence
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Microsoft.VisualStudio.SolutionPersistence.Serializer.SlnV12
open Microsoft.VisualStudio.SolutionPersistence.Serializer.Xml

type PlannedSolutionEdit =
    { Request: WorkspaceEditPreviewRequest
      Contents: byte array
      BackingPath: WorkspaceArtifactPath
      FileRename: PlannedProjectFileRename option }

and PlannedProjectFileRename =
    { Source: WorkspaceArtifactPath
      Destination: WorkspaceArtifactPath }
