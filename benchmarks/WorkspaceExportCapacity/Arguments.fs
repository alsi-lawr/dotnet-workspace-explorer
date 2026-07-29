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

module internal Arguments =
    let fail message =
        raise (InvalidOperationException message)

    let require condition message =
        if not condition then
            fail message

    let repositoryRoot =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let parsePositive name (value: string) =
        match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, number when number > 0 -> number
        | _ -> fail $"{name} must be a positive integer."

    let parseArguments arguments =
        let rec parse configuration remaining =
            match remaining with
            | [] -> configuration
            | "--configuration" :: value :: tail when value = "Debug" || value = "Release" ->
                parse
                    { configuration with
                        BuildConfiguration = value }
                    tail
            | "--projects" :: value :: tail ->
                parse
                    { configuration with
                        Projects = parsePositive "--projects" value }
                    tail
            | "--items" :: value :: tail ->
                parse
                    { configuration with
                        ItemsPerProject = parsePositive "--items" value }
                    tail
            | "--workers" :: value :: tail ->
                let capacities =
                    value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (parsePositive "--workers")
                    |> Array.distinct

                require (capacities.Length > 0) "--workers requires at least one capacity."

                parse
                    { configuration with
                        WorkerCapacities = capacities }
                    tail
            | "--output" :: value :: tail when not (String.IsNullOrWhiteSpace value) ->
                parse
                    { configuration with
                        OutputPath = Path.GetFullPath value }
                    tail
            | _ ->
                fail (
                    "Usage: dotnet run --project benchmarks/WorkspaceExportCapacity -c Release -- "
                    + "--configuration Release [--projects 12] [--items 40] "
                    + "[--workers 1,3] [--output path]"
                )

        let defaultOutput =
            Path.Combine(
                repositoryRoot,
                ".agent-workspace",
                "benchmarks",
                $"system-capacity-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json"
            )

        parse
            { BuildConfiguration = "Release"
              Projects = 12
              ItemsPerProject = 40
              WorkerCapacities = [| 1; 3 |]
              OutputPath = defaultOutput }
            (arguments |> Array.toList)

    let apphostPath configuration =
        let executable =
            if OperatingSystem.IsWindows() then
                "Dotnet.WorkspaceExplorer.exe"
            else
                "Dotnet.WorkspaceExplorer"

        Path.Combine(
            repositoryRoot,
            "src",
            "WorkspaceExplorer",
            "bin",
            configuration,
            "net10.0",
            executable
        )
