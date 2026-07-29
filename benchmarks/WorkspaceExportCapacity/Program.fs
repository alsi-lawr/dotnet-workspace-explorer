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

module Program =
    [<EntryPoint>]
    let main arguments =
        Arguments.require
            (OperatingSystem.IsLinux() && Directory.Exists "/proc")
            "Aggregate apphost-plus-worker RSS measurement Arguments.requires Linux /proc."

        let configuration = Arguments.parseArguments arguments

        let results =
            configuration.WorkerCapacities
            |> Array.map (WorkspaceExportMeasurement.measure configuration)

        let report =
            { SchemaVersion = 1
              CreatedAtUtc = DateTime.UtcNow
              Runtime = Environment.Version.ToString()
              OperatingSystem = Environment.OSVersion.ToString()
              ProcessorCount = Environment.ProcessorCount
              Projects = configuration.Projects
              ItemsPerProject = configuration.ItemsPerProject
              Results = results }

        Path.GetDirectoryName configuration.OutputPath
        |> Option.ofObj
        |> Option.filter (String.IsNullOrEmpty >> not)
        |> Option.iter (Directory.CreateDirectory >> ignore)

        File.WriteAllText(
            configuration.OutputPath,
            JsonSerializer.Serialize(report, JsonSerializerOptions(WriteIndented = true))
        )

        printfn "System capacity results: %s" configuration.OutputPath

        for result in results do
            printfn
                "workers=%d root=%.3f ms export=%.3f ms total=%.3f ms peak-rss=%d bytes processes=%d"
                result.WorkerCapacity
                result.RootMilliseconds
                result.ExportMilliseconds
                result.TotalMilliseconds
                result.PeakAggregateRssBytes
                result.PeakProcessCount

        0
