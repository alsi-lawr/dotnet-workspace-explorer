namespace Dotnet.CLI.Plus

open System
open System.IO
open Dotnet.CLI.Plus.Core

[<RequireQualifiedAccess>]
type internal ProjectFolderAction =
    | CreateDirectory of path: string
    | CopyDirectory of source: string * destination: string

type internal AppliedProjectFolderAction =
    | CreatedDirectory of path: string
    | CopiedDirectory of source: string * destination: string * fingerprint: string

module internal ProjectFolderActions =
    let private pathArgument name (request: MutationPreviewRequest) =
        request.Arguments.Values
        |> Seq.tryPick (fun argument ->
            if argument.ParameterId.Value = name then
                match argument.Value with
                | Path value -> Some value.Value
                | _ -> None
            else
                None)

    let bind projectDirectory (request: MutationPreviewRequest) =
        let resolve value =
            Path.GetFullPath(value, projectDirectory)

        match request.CommandId.Value with
        | "project.folder.new" ->
            match pathArgument "path" request with
            | Some value -> Ok [| ProjectFolderAction.CreateDirectory(resolve value) |]
            | None -> Error "The folder path is missing."
        | "project.folder.copy" ->
            match pathArgument "source" request, pathArgument "path" request with
            | Some source, Some destination ->
                [| ProjectFolderAction.CopyDirectory(resolve source, resolve destination) |]
                |> Ok
            | _ -> Error "The folder source or destination is missing."
        | _ -> Ok Array.empty

    let paths =
        function
        | ProjectFolderAction.CreateDirectory path ->
            [ path; ProjectFolderPaths.destinationParent path ]
        | ProjectFolderAction.CopyDirectory(source, destination) ->
            [ source; destination; ProjectFolderPaths.destinationParent destination ]

    let writeDigest (writer: BinaryWriter) =
        function
        | ProjectFolderAction.CreateDirectory path ->
            CanonicalBinary.writeValue writer "create-directory"
            CanonicalBinary.writeValue writer path
        | ProjectFolderAction.CopyDirectory(source, destination) ->
            CanonicalBinary.writeValue writer "copy-directory"
            CanonicalBinary.writeValue writer source
            CanonicalBinary.writeValue writer destination

    let execute =
        function
        | ProjectFolderAction.CreateDirectory path ->
            Directory.CreateDirectory path |> ignore
            AppliedProjectFolderAction.CreatedDirectory path
        | ProjectFolderAction.CopyDirectory(source, destination) ->
            let expected = MutationFiles.fingerprint source |> Result.defaultWith invalidOp

            MutationFiles.copyNoFollow source destination

            match MutationFiles.fingerprint destination with
            | Ok actual when actual = expected ->
                AppliedProjectFolderAction.CopiedDirectory(source, destination, expected)
            | Ok _ -> invalidOp "The copied folder did not verify."
            | Error error -> invalidOp error

    let compensate =
        function
        | AppliedProjectFolderAction.CreatedDirectory path ->
            MutationFiles.remove path

            if MutationFiles.exists path then
                invalidOp "The created folder remained after compensation."
        | AppliedProjectFolderAction.CopiedDirectory(_, destination, expected) ->
            match MutationFiles.fingerprint destination with
            | Ok actual when actual = expected -> MutationFiles.remove destination
            | Ok _ -> invalidOp "The copied folder changed before compensation."
            | Error error -> invalidOp error

            if MutationFiles.exists destination then
                invalidOp "The copied folder remained after compensation."
