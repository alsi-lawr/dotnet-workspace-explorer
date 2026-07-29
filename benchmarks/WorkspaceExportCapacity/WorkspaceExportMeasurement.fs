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

module internal WorkspaceExportMeasurement =
    let millisecondsSince timestamp =
        float (Stopwatch.GetTimestamp() - timestamp) * 1000.0
        / float Stopwatch.Frequency

    let measure (configuration: ExportCapacityOptions) workerCapacity =
        let apphost = Arguments.apphostPath configuration.BuildConfiguration

        Arguments.require
            (File.Exists apphost)
            $"Build the {configuration.BuildConfiguration} apphost first: {apphost}"

        let corpus =
            Path.Combine(
                Arguments.repositoryRoot,
                ".agent-workspace",
                "benchmarks",
                $"corpus-{workerCapacity}-{Guid.NewGuid():N}"
            )

        let solution =
            WorkspaceCorpus.write corpus configuration.Projects configuration.ItemsPerProject

        let start = ProcessStartInfo apphost
        start.WorkingDirectory <- corpus
        start.UseShellExecute <- false
        start.RedirectStandardInput <- true
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.CreateNoWindow <- true

        for argument in
            [ "solution"; solution; "--pipe"; "--export-workers"; string workerCapacity ] do
            start.ArgumentList.Add argument

        use child = new Process(StartInfo = start)
        let mutable sampler = None

        try
            let totalStarted = Stopwatch.GetTimestamp()
            Arguments.require (child.Start()) "Could not start the built apphost."
            let stderr = child.StandardError.ReadToEndAsync()
            sampler <- Some(new ProcessTreeRssSampler(child.Id))

            WorkspaceRpcClient.send
                child
                (WorkspaceRpcClient.request
                    1u
                    "WorkspaceRpcClient.initialize"
                    WorkspaceRpcClient.initialize)

            WorkspaceRpcClient.readFrame child |> WorkspaceRpcClient.response 1u |> ignore

            WorkspaceRpcClient.send
                child
                (WorkspaceRpcClient.request 2u "workspace/root" RpcValue.emptyMap)

            let root = WorkspaceRpcClient.readFrame child |> WorkspaceRpcClient.response 2u

            WorkspaceRpcClient.field "revision" root
            |> RpcValue.requireInteger "revision"
            |> ignore

            let rootMilliseconds = millisecondsSince totalStarted
            let exportStarted = Stopwatch.GetTimestamp()

            WorkspaceRpcClient.send
                child
                (WorkspaceRpcClient.request 3u "workspace/export/start" RpcValue.emptyMap)

            let export = WorkspaceRpcClient.readFrame child |> WorkspaceRpcClient.response 3u

            let operationId =
                WorkspaceRpcClient.field "operationId" export
                |> RpcValue.requireString "operationId"

            let mutable nodes = 0
            let mutable chunks = 0
            let mutable completed = false

            while not completed do
                match WorkspaceRpcClient.readFrame child with
                | Notification("workspace/export/chunk", parameters) ->
                    Arguments.require
                        (WorkspaceRpcClient.field "operationId" parameters = RpcValue.String
                            operationId)
                        "The export stream changed operation identity."

                    nodes <-
                        nodes
                        + (WorkspaceRpcClient.field "nodes" parameters
                           |> RpcValue.requireArray "nodes"
                           |> _.Length)

                    chunks <- chunks + 1
                | Notification("workspace/operations/completed", parameters) ->
                    Arguments.require
                        (WorkspaceRpcClient.field "operationId" parameters = RpcValue.String
                            operationId)
                        "The completion changed operation identity."

                    Arguments.require
                        (WorkspaceRpcClient.field "outcome" parameters = RpcValue.String "succeeded")
                        "The measured export did not succeed."

                    completed <- true
                | frame -> Arguments.fail $"Unexpected export frame: {frame}"

            let exportMilliseconds = millisecondsSince exportStarted

            WorkspaceRpcClient.send
                child
                (WorkspaceRpcClient.request 4u "shutdown" RpcValue.emptyMap)

            let shutdown = WorkspaceRpcClient.readFrame child |> WorkspaceRpcClient.response 4u

            Arguments.require
                (WorkspaceRpcClient.field "accepted" shutdown = RpcValue.Boolean true)
                "Shutdown was not accepted."

            child.StandardInput.Close()

            Arguments.require
                (child.WaitForExit 30000)
                "The measured apphost did not exit after shutdown."

            Arguments.require
                (child.ExitCode = 0)
                $"The measured apphost exited {child.ExitCode}: {stderr.Result}"

            let totalMilliseconds = millisecondsSince totalStarted
            let peakRss, peakProcesses, sampleCount = sampler.Value.Stop()
            sampler <- None

            { WorkerCapacity = workerCapacity
              RootMilliseconds = rootMilliseconds
              ExportMilliseconds = exportMilliseconds
              TotalMilliseconds = totalMilliseconds
              ExportedNodeCount = nodes
              ExportChunkCount = chunks
              PeakAggregateRssBytes = peakRss
              PeakProcessCount = peakProcesses
              RssSamples = sampleCount }
        finally
            sampler |> Option.iter (fun value -> value.Stop() |> ignore)

            if not child.HasExited then
                child.Kill true
                child.WaitForExit()

            if Directory.Exists corpus then
                Directory.Delete(corpus, true)
