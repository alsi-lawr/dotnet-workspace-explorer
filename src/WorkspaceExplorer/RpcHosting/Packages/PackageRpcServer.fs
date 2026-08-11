namespace Dotnet.WorkspaceExplorer

#nowarn "3511"

open System.Collections.Immutable
open System.IO
open System.Threading
open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Rpc

[<RequireQualifiedAccess>]
module internal PackageRpcServer =
    let runWithPortsAsync
        target
        ports
        (input: Stream)
        (output: Stream)
        (error: TextWriter)
        (cancellationToken: CancellationToken)
        =
        let state = PackageRpcState(target, ports)
        let negotiation = PackageRpcNegotiation()

        let initialize parameters _ =
            task {
                match PackageRpc.parseInitialize parameters with
                | Error rpcError -> return Error rpcError
                | Ok request ->
                    negotiation.MaximumFrameBytes <- request.MaximumFrameBytes
                    negotiation.MaximumPageSize <- request.MaximumPageSize

                    negotiation.Capabilities <-
                        request.Capabilities
                        |> Seq.filter PackageRpcContract.capabilities.Contains
                        |> ImmutableHashSet.CreateRange

                    state.ReadmeEnabled <- negotiation.Capabilities.Contains "packages.readme.v1"

                    return Ok(PackageRpcResponses.initializeResult target request)
            }

        let dispatch _ methodName parameters requestCancellationToken =
            task {
                match PackageRpcContract.capabilityForMethod methodName with
                | Some capability when not (negotiation.Capabilities.Contains capability) ->
                    return
                        Error(
                            RpcErrors.unsupported
                                "The required package capability was not negotiated."
                        )
                | _ ->
                    let requiredCompanion =
                        match methodName with
                        | "package/execute/start"
                        | "package/executeBatch/start" -> Some "packages.partial-recovery.v1"
                        | _ -> None

                    match requiredCompanion with
                    | Some capability when not (negotiation.Capabilities.Contains capability) ->
                        return
                            Error(
                                RpcErrors.unsupported
                                    "The required package capability was not negotiated."
                            )
                    | _ ->
                        match PackageRpc.parseRequest methodName parameters with
                        | Error rpcError -> return Error rpcError
                        | Ok request ->
                            return!
                                PackageRpcDispatch.dispatch
                                    state
                                    negotiation.MaximumFrameBytes
                                    negotiation.MaximumPageSize
                                    request
                                    requestCancellationToken
            }

        let configuration =
            { Profile = PackageRpcContract.current
              Limits = MessagePackRpcCodec.secureLimits
              GetOutboundFrameLimit = fun () -> negotiation.MaximumFrameBytes
              Initialize = initialize
              Dispatch = dispatch }

        RpcSession.runAsync configuration input output error cancellationToken

    let runAsync target input output error cancellationToken =
        runWithPortsAsync
            target
            (NuGetPackageCatalog.create ())
            input
            output
            error
            cancellationToken
