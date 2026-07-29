namespace Dotnet.WorkspaceExplorer.WorkspaceExportCapacity

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Dotnet.WorkspaceExplorer.Rpc

type ExportCapacityOptions =
    { BuildConfiguration: string
      Projects: int
      ItemsPerProject: int
      WorkerCapacities: int array
      OutputPath: string }

type ExportCapacityMeasurement =
    { WorkerCapacity: int
      RootMilliseconds: float
      ExportMilliseconds: float
      TotalMilliseconds: float
      ExportedNodeCount: int
      ExportChunkCount: int
      PeakAggregateRssBytes: int64
      PeakProcessCount: int
      RssSamples: int }

type ExportCapacityReport =
    { SchemaVersion: int
      CreatedAtUtc: DateTime
      Runtime: string
      OperatingSystem: string
      ProcessorCount: int
      Projects: int
      ItemsPerProject: int
      Results: ExportCapacityMeasurement array }
