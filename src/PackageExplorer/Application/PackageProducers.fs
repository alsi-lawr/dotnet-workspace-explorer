namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Packages

[<RequireQualifiedAccess>]
module PackageProducer =
    let internal cancellable
        (requests: ConcurrentDictionary<PackageRequestId, CancellationTokenSource>)
        requestId
        duplicateFailure
        cancelledFailure
        operation
        =
        async {
            let! ambient = Async.CancellationToken
            use cancellation = CancellationTokenSource.CreateLinkedTokenSource ambient

            if not (requests.TryAdd(requestId, cancellation)) then
                return Error duplicateFailure
            else
                try
                    let completion =
                        TaskCompletionSource<_> TaskCreationOptions.RunContinuationsAsynchronously

                    let completeCancelled () =
                        completion.TrySetResult(Error cancelledFailure) |> ignore

                    Async.StartWithContinuations(
                        operation cancellation.Token,
                        (fun result -> completion.TrySetResult result |> ignore),
                        (fun error ->
                            if cancellation.IsCancellationRequested then
                                completeCancelled ()
                            else
                                completion.TrySetException error |> ignore),
                        (fun _ -> completeCancelled ()),
                        cancellationToken = CancellationToken.None
                    )

                    return! completion.Task |> Async.AwaitTask
                finally
                    requests.TryRemove requestId |> ignore
        }

    let collect producer request =
        async {
            let items = ResizeArray()

            let sink _ batch =
                async { items.AddRange(NonEmptyList.toList batch) }

            let! outcome = producer request sink

            return outcome |> Result.map (fun completion -> items |> Seq.toList, completion)
        }
