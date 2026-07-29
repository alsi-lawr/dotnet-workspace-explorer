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

[<CollectionDefinition("Core contracts")>]
type CorecontractsCollection() = class end

[<CollectionDefinition("Workspace edits")>]
type WorkspaceeditsCollection() = class end

[<CollectionDefinition("Solution contracts")>]
type SolutioncontractsCollection() = class end

[<CollectionDefinition("Solution edits")>]
type SolutioneditsCollection() = class end
