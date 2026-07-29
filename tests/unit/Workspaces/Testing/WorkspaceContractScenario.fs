namespace Dotnet.WorkspaceExplorer.Workspaces.UnitTests

#nowarn "3261"

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Xml.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open FsUnit.Xunit
open Xunit

module private WorkspaceContractScenario =
    let workspace format =
        WorkspaceDescriptor.Create(
            WorkspacePath.Create "/tmp/dotnet-workspace-explorer/Demo.slnx",
            FileSystemCaseSensitivity.Sensitive,
            format,
            WorkspaceRevision.Create 1L,
            WorkspaceAccess.ReadWrite
        )

    let diagnostic () =
        WorkspaceDiagnostic.CreateSimple(
            WorkspaceDiagnosticSeverity.Error,
            WorkspaceDiagnosticCode.Create "workspace.test",
            "Safe test diagnostic.",
            false,
            CorrelationId.New()
        )
