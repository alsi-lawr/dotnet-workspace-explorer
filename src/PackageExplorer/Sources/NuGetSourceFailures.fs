namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open Dotnet.WorkspaceExplorer.Packages

[<RequireQualifiedAccess>]
module internal NuGetSourceFailures =
    let packageFailure kind message retry =
        PackageFailure.create kind message retry |> Result.defaultWith (failwithf "%A")

    let invalidRequest message =
        packageFailure PackageFailureKind.InvalidRequest message PackageFailureRetry.Never

    let cancelled () =
        packageFailure
            PackageFailureKind.Cancelled
            "The package request was cancelled."
            PackageFailureRetry.Never

    let private statusCode (error: exn) =
        let rec find (current: exn) =
            match current with
            | :? HttpRequestException as requestError ->
                requestError.StatusCode |> Option.ofNullable
            | _ -> current.InnerException |> Option.ofObj |> Option.bind find

        find error

    let sourceFailure source (error: exn) =
        let rec containsNetworkFailure (current: exn) =
            (current :? HttpRequestException)
            || (current.InnerException |> Option.ofObj |> Option.exists containsNetworkFailure)

        let rec containsMalformed (current: exn) =
            (current :? JsonException)
            || (current :? InvalidDataException)
            || current.GetType().Name.Contains("ProtocolException", StringComparison.Ordinal)
            || (current.InnerException |> Option.ofObj |> Option.exists containsMalformed)

        let kind =
            match statusCode error with
            | Some HttpStatusCode.Unauthorized -> PackageSourceFailureKind.AuthenticationRequired
            | Some HttpStatusCode.Forbidden -> PackageSourceFailureKind.Unauthorized
            | _ when containsNetworkFailure error -> PackageSourceFailureKind.Unavailable
            | _ when containsMalformed error -> PackageSourceFailureKind.Malformed
            | _ -> PackageSourceFailureKind.Unavailable

        PackageSourceFailure.create source kind

    let sourceFailureAsPackageFailure failure =
        match PackageSourceFailure.kind failure with
        | PackageSourceFailureKind.AuthenticationRequired ->
            packageFailure
                PackageFailureKind.AuthenticationRequired
                (PackageSourceFailure.message failure)
                PackageFailureRetry.AfterUserAction
        | PackageSourceFailureKind.Unauthorized ->
            packageFailure
                PackageFailureKind.Unauthorized
                (PackageSourceFailure.message failure)
                PackageFailureRetry.AfterUserAction
        | PackageSourceFailureKind.Malformed ->
            packageFailure
                PackageFailureKind.MalformedSource
                (PackageSourceFailure.message failure)
                PackageFailureRetry.Transient
        | PackageSourceFailureKind.Unavailable ->
            packageFailure
                PackageFailureKind.SourceUnavailable
                (PackageSourceFailure.message failure)
                PackageFailureRetry.Transient
