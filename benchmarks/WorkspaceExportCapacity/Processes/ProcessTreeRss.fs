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

module internal ProcessTreeRss =
    let procSnapshot () =
        Directory.EnumerateDirectories "/proc"
        |> Seq.choose (fun directory ->
            let name = Path.GetFileName directory

            match Int32.TryParse name with
            | false, _ -> None
            | true, pid ->
                try
                    let stat = File.ReadAllText(Path.Combine(directory, "stat"))
                    let afterName = stat.LastIndexOf ')'

                    if afterName < 0 then
                        None
                    else
                        let fields =
                            stat[afterName + 2 ..]
                                .Split(' ', StringSplitOptions.RemoveEmptyEntries)

                        let parent = Int32.Parse(fields[1], CultureInfo.InvariantCulture)

                        let rss =
                            File.ReadLines(Path.Combine(directory, "status"))
                            |> Seq.tryFind (fun line ->
                                line.StartsWith("VmRSS:", StringComparison.Ordinal))
                            |> Option.map (fun line ->
                                line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]
                                |> Int64.Parse
                                |> fun kibibytes -> kibibytes * 1024L)
                            |> Option.defaultValue 0L

                        Some(pid, parent, rss)
                with
                | :? IOException
                | :? UnauthorizedAccessException
                | :? InvalidOperationException
                | :? FormatException
                | :? IndexOutOfRangeException -> None)
        |> Seq.toArray

type ProcessTreeRssSampler(rootPid: int) =
    let cancellation = new CancellationTokenSource()
    let mutable peakBytes = 0L
    let mutable peakProcesses = 0
    let mutable samples = 0

    let thread =
        Thread(
            ThreadStart(fun () ->
                while not cancellation.IsCancellationRequested do
                    let snapshot = ProcessTreeRss.procSnapshot ()
                    let tree = HashSet<int>()
                    tree.Add rootPid |> ignore
                    let mutable changed = true

                    while changed do
                        changed <- false

                        for pid, parent, _ in snapshot do
                            if tree.Contains parent && tree.Add pid then
                                changed <- true

                    let aggregate =
                        snapshot
                        |> Array.sumBy (fun (pid, _, rss) -> if tree.Contains pid then rss else 0L)

                    peakBytes <- max peakBytes aggregate
                    peakProcesses <- max peakProcesses tree.Count
                    samples <- samples + 1
                    Thread.Sleep 10)
        )

    do
        thread.IsBackground <- true
        thread.Start()

    member _.Stop() =
        cancellation.Cancel()
        thread.Join()
        cancellation.Dispose()
        peakBytes, peakProcesses, samples
