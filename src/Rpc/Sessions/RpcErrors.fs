namespace Dotnet.WorkspaceExplorer.Rpc

open Dotnet.WorkspaceExplorer.Workspaces

#nowarn "3511"

open System
open System.IO
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
module RpcErrors =
    let create code message data =
        { Code = code
          Message = message
          Data = data }

    let invalidParams message = create "invalid_params" message None
    let invalidRequest message = create "invalid_request" message None

    let preInitialize =
        create "not_initialized" "initialize must be called before other methods." None

    let unknownMethod name =
        create
            "unknown_method"
            $"The method '{name}' is not available in this protocol profile."
            None

    let unsupported message =
        create "unsupported_capability" message None

    let internalError =
        create "internal_error" "The request could not be completed safely." None

    let responseTooLarge =
        create "response_too_large" "The response exceeds the negotiated outbound frame limit." None
