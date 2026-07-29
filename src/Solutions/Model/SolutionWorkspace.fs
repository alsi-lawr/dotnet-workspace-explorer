namespace Dotnet.WorkspaceExplorer.Solutions

open Dotnet.WorkspaceExplorer.Workspaces

open System.Collections.Immutable
open System.Collections.Generic
open Dotnet.WorkspaceExplorer.Workspaces

type SolutionWorkspace =
    private
        { DescriptorValue: WorkspaceDescriptor
          SolutionPathValue: WorkspaceArtifactPath
          ContentsValue: SolutionContents }

    member this.Descriptor = this.DescriptorValue
    member this.SolutionPath = this.SolutionPathValue
    member this.Contents = this.ContentsValue

    static member internal Create
        (
            descriptor: WorkspaceDescriptor,
            backingSolutionPath: WorkspaceArtifactPath,
            root: SolutionContents
        ) =
        { DescriptorValue = descriptor
          SolutionPathValue = backingSolutionPath
          ContentsValue = root }
