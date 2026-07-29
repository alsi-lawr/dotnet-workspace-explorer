namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex

open System.IO
open Dotnet.WorkspaceExplorer.Workspaces

[<RequireQualifiedAccess>]
type internal ProjectFolderAction =
    | CreateDirectory of path: string
    | CopyDirectory of source: string * destination: string

type internal AppliedProjectFolderAction =
    | CreatedDirectory of path: string
    | CopiedDirectory of source: string * destination: string * fingerprint: string

module internal ProjectFolderActions =
    let private pathArgument name (request: WorkspaceEditPreviewRequest) =
        request.Arguments.Values
        |> Seq.tryPick (fun argument ->
            if argument.ParameterId.Value = name then
                match argument.Value with
                | Path value -> Some value.Value
                | _ -> None
            else
                None)

    let bind projectDirectory (request: WorkspaceEditPreviewRequest) =
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
            WorkspaceEditFingerprint.writeValue writer "create-directory"
            WorkspaceEditFingerprint.writeValue writer path
        | ProjectFolderAction.CopyDirectory(source, destination) ->
            WorkspaceEditFingerprint.writeValue writer "copy-directory"
            WorkspaceEditFingerprint.writeValue writer source
            WorkspaceEditFingerprint.writeValue writer destination

    let execute =
        function
        | ProjectFolderAction.CreateDirectory path ->
            Directory.CreateDirectory path |> ignore
            CreatedDirectory path
        | ProjectFolderAction.CopyDirectory(source, destination) ->
            let expected = ArtifactFiles.fingerprint source |> Result.defaultWith invalidOp

            ArtifactFiles.copyNoFollow source destination

            match ArtifactFiles.fingerprint destination with
            | Ok actual when actual = expected -> CopiedDirectory(source, destination, expected)
            | Ok _ -> invalidOp "The copied folder did not verify."
            | Error error -> invalidOp error

    let compensate =
        function
        | CreatedDirectory path ->
            ArtifactFiles.remove path

            if ArtifactFiles.exists path then
                invalidOp "The created folder remained after compensation."
        | CopiedDirectory(_, destination, expected) ->
            match ArtifactFiles.fingerprint destination with
            | Ok actual when actual = expected -> ArtifactFiles.remove destination
            | Ok _ -> invalidOp "The copied folder changed before compensation."
            | Error error -> invalidOp error

            if ArtifactFiles.exists destination then
                invalidOp "The copied folder remained after compensation."
