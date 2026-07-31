namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open Dotnet.WorkspaceExplorer.Workspaces

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading

exception private WorkspaceEditFailed of WorkspaceFailure

type NativeArtifactTrash private () =
    static member CreateForCurrentUser() : ArtifactTrash =
        if OperatingSystem.IsWindows() then
            WindowsArtifactTrash()
        elif OperatingSystem.IsMacOS() then
            MacArtifactTrash()
        else
            let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

            let data =
                match Environment.GetEnvironmentVariable "XDG_DATA_HOME" with
                | null
                | "" -> Path.Combine(home, ".local", "share")
                | value -> value

            FreedesktopArtifactTrash data

type private ConfirmedWorkspaceEdit =
    { Digest: string
      ExpiresAtUtc: DateTimeOffset
      Fingerprints: Map<string, string> }

type WorkspaceEditTransaction
    (
        workspaceRoot: WorkspaceArtifactPath,
        clock: TimeProvider,
        currentRevision: unit -> WorkspaceRevision,
        trash: ArtifactTrash
    ) =
    let gate = obj ()
    let previews = Dictionary<string, ConfirmedWorkspaceEdit> StringComparer.Ordinal

    let diagnostic code message =
        WorkspaceDiagnostic.CreateSimple(
            WorkspaceDiagnosticSeverity.Error,
            WorkspaceDiagnosticCode.Create code,
            message,
            false,
            CorrelationId.New()
        )

    let invalid message =
        Failure(InvalidInput("mutation", diagnostic "invalid_input" message))

    let unsupported message =
        UnsupportedCapability(
            WorkspaceCapabilityId.Write,
            diagnostic "unsupported_capability" message
        )

    let conflict expected actual message =
        Failure(Conflict(expected, actual, diagnostic "workspace_conflict" message))

    let internalFailure message =
        Internal(diagnostic "mutation_failed" message)

    let argumentText argument =
        match argument.Value with
        | Text value -> $"text:{value}"
        | Path value -> $"path:{value.Value}"
        | Boolean value -> $"bool:{value}"
        | Integer value -> $"int:{value}"
        | Node value -> $"node:{value.Value}"
        | Choice value -> $"choice:{value.Value}"
        | TextArray values ->
            use stream = new MemoryStream()
            use writer = new BinaryWriter(stream, Encoding.UTF8, true)
            WorkspaceEditFingerprint.writeSection writer "texts" values.Length

            for value in values do
                WorkspaceEditFingerprint.writeValue writer value

            writer.Flush()
            $"texts:{Convert.ToHexString(stream.ToArray())}"
        | NodeIdArray values ->
            use stream = new MemoryStream()
            use writer = new BinaryWriter(stream, Encoding.UTF8, true)
            WorkspaceEditFingerprint.writeSection writer "nodes" values.Length

            for value in values do
                WorkspaceEditFingerprint.writeValue writer value.Value

            writer.Flush()
            $"nodes:{Convert.ToHexString(stream.ToArray())}"

    let actionPaths =
        function
        | WorkspaceEditAction.CreateDirectory path
        | WorkspaceEditAction.ReplaceFile(path, _)
        | WorkspaceEditAction.Delete(path, _, _)
        | WorkspaceEditAction.Trash path -> [ path ]
        | WorkspaceEditAction.Rename(source, destination)
        | WorkspaceEditAction.Move(source, destination)
        | WorkspaceEditAction.Copy(source, destination) -> [ source; destination ]

    let bind request (actions: WorkspaceEditAction array) =
        let workspaceRoot = ArtifactFiles.canonicalNoFollow false workspaceRoot.Value

        let roots =
            request.AuthorizedRoots
            |> Seq.map (fun path -> ArtifactFiles.canonicalNoFollow false path.Value)
            |> Seq.toArray

        let targets =
            request.Targets
            |> Seq.map (fun path -> ArtifactFiles.canonicalNoFollow true path.Value)
            |> Seq.toArray

        let failure results =
            results
            |> Array.tryPick (function
                | Error error -> Some error
                | Ok _ -> None)

        match workspaceRoot, failure roots, failure targets with
        | Error error, _, _
        | _, Some error, _
        | _, _, Some error -> Error error
        | Ok canonicalWorkspaceRoot, None, None ->
            let canonicalRoots = roots |> Array.choose Result.toOption
            let canonicalTargets = targets |> Array.choose Result.toOption

            let authorized path =
                ArtifactFiles.isUnder canonicalWorkspaceRoot path
                || canonicalRoots |> Array.exists (fun root -> ArtifactFiles.isUnder root path)

            let projectDirectory =
                canonicalTargets
                |> Array.tryHead
                |> Option.map Path.GetDirectoryName
                |> Option.bind Option.ofObj
                |> Option.defaultValue canonicalWorkspaceRoot

            let folderActions = ProjectFolderActions.bind projectDirectory request

            let planPaths =
                match folderActions with
                | Error _ -> [||]
                | Ok bound ->
                    Seq.append
                        (actions |> Seq.collect actionPaths)
                        (bound |> Seq.collect ProjectFolderActions.paths)
                    |> Seq.map (ArtifactFiles.canonicalNoFollow true)
                    |> Seq.toArray

            match folderActions, failure planPaths with
            | Error error, _ -> Error error
            | _, Some error -> Error error
            | Ok folderActions, None ->
                let plannedPaths = planPaths |> Array.choose Result.toOption

                if canonicalTargets |> Array.exists (authorized >> not) then
                    Error "Every target requires an explicit authorization root."
                elif
                    canonicalTargets
                    |> Array.exists (fun path ->
                        not (ArtifactFiles.isUnder canonicalWorkspaceRoot path))
                    && not (request.Intents.Contains WorkspaceEditIntent.AccessExternalPath)
                then
                    Error "External paths require explicit external-path intent."
                elif
                    plannedPaths
                    |> Array.exists (fun path ->
                        not (authorized path && Array.contains path canonicalTargets))
                then
                    Error "Every action path must be an explicitly authorized target."
                else
                    let fingerprints =
                        canonicalTargets
                        |> Array.map (fun path ->
                            ArtifactFiles.fingerprint path |> Result.map (fun value -> path, value))

                    match failure fingerprints with
                    | Some error -> Error error
                    | None ->
                        use stream = new MemoryStream()
                        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
                        WorkspaceEditFingerprint.writeSection writer "command" 1
                        WorkspaceEditFingerprint.writeValue writer request.CommandId.Value
                        WorkspaceEditFingerprint.writeSection writer "revision" 1
                        writer.Write request.ExpectedRevision.Value
                        WorkspaceEditFingerprint.writeSection writer "workspace-root" 1
                        WorkspaceEditFingerprint.writeValue writer canonicalWorkspaceRoot

                        WorkspaceEditFingerprint.writeSection
                            writer
                            "targets"
                            canonicalTargets.Length

                        canonicalTargets |> Array.iter (WorkspaceEditFingerprint.writeValue writer)

                        let arguments =
                            request.Arguments.Values
                            |> Seq.sortBy _.ParameterId.Value
                            |> Seq.toArray

                        WorkspaceEditFingerprint.writeSection writer "arguments" arguments.Length

                        for argument in arguments do
                            WorkspaceEditFingerprint.writeValue writer argument.ParameterId.Value
                            WorkspaceEditFingerprint.writeValue writer (argumentText argument)

                        let intents = request.Intents |> Seq.map int |> Seq.sort |> Seq.toArray
                        WorkspaceEditFingerprint.writeSection writer "intents" intents.Length
                        intents |> Array.iter writer.Write
                        let orderedRoots = canonicalRoots |> Array.sort
                        WorkspaceEditFingerprint.writeSection writer "roots" orderedRoots.Length
                        orderedRoots |> Array.iter (WorkspaceEditFingerprint.writeValue writer)
                        WorkspaceEditFingerprint.writeSection writer "actions" actions.Length
                        let mutable pathIndex = 0

                        let nextPath () =
                            let path = plannedPaths[pathIndex]
                            pathIndex <- pathIndex + 1
                            path

                        for action in actions do
                            match action with
                            | WorkspaceEditAction.CreateDirectory _path ->
                                WorkspaceEditFingerprint.writeValue writer "mkdir"
                                WorkspaceEditFingerprint.writeValue writer (nextPath ())
                            | WorkspaceEditAction.ReplaceFile(_path, contents) ->
                                WorkspaceEditFingerprint.writeValue writer "replace"
                                WorkspaceEditFingerprint.writeValue writer (nextPath ())

                                WorkspaceEditFingerprint.writeValue
                                    writer
                                    (SHA256.HashData contents |> Convert.ToHexString)
                            | WorkspaceEditAction.Rename(_source, _destination) ->
                                WorkspaceEditFingerprint.writeValue writer "rename"
                                WorkspaceEditFingerprint.writeValue writer (nextPath ())
                                WorkspaceEditFingerprint.writeValue writer (nextPath ())
                            | WorkspaceEditAction.Move(_source, _destination) ->
                                WorkspaceEditFingerprint.writeValue writer "move"
                                WorkspaceEditFingerprint.writeValue writer (nextPath ())
                                WorkspaceEditFingerprint.writeValue writer (nextPath ())
                            | WorkspaceEditAction.Copy(_source, _destination) ->
                                WorkspaceEditFingerprint.writeValue writer "copy"
                                WorkspaceEditFingerprint.writeValue writer (nextPath ())
                                WorkspaceEditFingerprint.writeValue writer (nextPath ())
                            | WorkspaceEditAction.Delete(_path, permanent, recursive) ->
                                WorkspaceEditFingerprint.writeValue writer "delete"
                                WorkspaceEditFingerprint.writeValue writer (nextPath ())
                                writer.Write permanent
                                writer.Write recursive
                            | WorkspaceEditAction.Trash _path ->
                                WorkspaceEditFingerprint.writeValue writer "trash"
                                WorkspaceEditFingerprint.writeValue writer (nextPath ())

                        WorkspaceEditFingerprint.writeSection
                            writer
                            "folder-actions"
                            folderActions.Length

                        folderActions |> Array.iter (ProjectFolderActions.writeDigest writer)
                        writer.Flush()

                        Ok(
                            SHA256.HashData(stream.ToArray()) |> Convert.ToHexString,
                            canonicalTargets,
                            fingerprints |> Array.choose Result.toOption |> Map.ofArray
                        )

    let validate
        (intents: ImmutableHashSet<WorkspaceEditIntent>)
        (actions: WorkspaceEditAction array)
        =
        let destinations = HashSet<string> StringComparer.Ordinal

        let repeatedDestination path =
            destinations.Add(ArtifactFiles.identity path) |> not

        let irreversible =
            function
            | WorkspaceEditAction.Trash _
            | WorkspaceEditAction.Delete _ -> true
            | _ -> false

        let trailing =
            actions |> Array.skipWhile (irreversible >> not) |> Array.forall irreversible

        trailing
        && actions
           |> Array.forall (function
               | WorkspaceEditAction.CreateDirectory path ->
                   not (ArtifactFiles.exists path || repeatedDestination path)
               | WorkspaceEditAction.ReplaceFile(destination, _) ->
                   not (ArtifactFiles.exists destination || repeatedDestination destination)
                   || intents.Contains WorkspaceEditIntent.Overwrite
               | WorkspaceEditAction.Rename(source, destination) ->
                   let repeated = repeatedDestination destination

                   ArtifactFiles.exists source
                   && (not (ArtifactFiles.exists destination)
                       || ArtifactFiles.isCaseOnlyRename source destination
                       || intents.Contains WorkspaceEditIntent.Overwrite)
                   && (not repeated || intents.Contains WorkspaceEditIntent.Overwrite)
               | WorkspaceEditAction.Move(source, destination) ->
                   let repeated = repeatedDestination destination

                   ArtifactFiles.exists source
                   && (not (ArtifactFiles.exists destination)
                       || intents.Contains WorkspaceEditIntent.Overwrite)
                   && (not repeated || intents.Contains WorkspaceEditIntent.Overwrite)
               | WorkspaceEditAction.Copy(source, destination) ->
                   let repeated = repeatedDestination destination

                   ArtifactFiles.exists source
                   && not (ArtifactFiles.exists destination)
                   && not repeated
               | WorkspaceEditAction.Delete(path, permanent, recursive) ->
                   ArtifactFiles.exists path
                   && (not permanent || intents.Contains WorkspaceEditIntent.PermanentDelete)
                   && (not recursive || intents.Contains WorkspaceEditIntent.RecursiveDelete)
                   && (not permanent || recursive || not (ArtifactFiles.nonEmptyDirectory path))
               | WorkspaceEditAction.Trash path -> ArtifactFiles.exists path)

    let reverseAll reversals =
        let remaining = ResizeArray<string>()

        for description, reverse in reversals do
            try
                reverse ()
            with ex ->
                remaining.Add $"{description}: {ex.Message}"

        remaining |> Seq.toList

    let cleanAll paths =
        let remaining = ResizeArray<string>()

        for path in paths do
            try
                ArtifactFiles.remove path

                if ArtifactFiles.exists path then
                    remaining.Add $"remove temporary artifact: {path}"
            with ex ->
                remaining.Add $"remove temporary artifact {path}: {ex.Message}"

        remaining |> Seq.toList

    let partial remaining =
        let detail = String.concat "; " remaining

        Failure(
            PartialRecoveryRequired(
                detail,
                diagnostic "partial_recovery_required" $"Compensation incomplete: {detail}"
            )
        )

    member _.Prepare(request: WorkspaceEditPreviewRequest, actions: seq<WorkspaceEditAction>) =
        lock gate (fun () ->
            let actual = currentRevision ()

            if actual.Value <> request.ExpectedRevision.Value then
                conflict
                    request.ExpectedRevision
                    actual
                    "The workspace revision changed before preview."
            else
                let plan = actions |> Seq.toArray

                match bind request plan with
                | Error error -> invalid error
                | Ok(_digest, _, _fingerprints) when not (validate request.Intents plan) ->
                    invalid
                        "The action plan lacks a required intent or valid irreversible ordering."
                | Ok(digest, _, fingerprints) ->
                    let token = Convert.ToHexString(RandomNumberGenerator.GetBytes 32)
                    let expiry = clock.GetUtcNow().AddMinutes 5.0

                    previews[token] <-
                        { Digest = digest
                          ExpiresAtUtc = expiry
                          Fingerprints = fingerprints }

                    Success
                        { Confirmation = WorkspaceEditConfirmation.Create token
                          ExpiresAtUtc = expiry })

    member private _.ExecuteCore
        (
            request: WorkspaceEditPreviewRequest,
            actions: seq<WorkspaceEditAction>,
            token: WorkspaceEditConfirmation,
            cancellationToken: CancellationToken,
            tryReserveCommit: unit -> bool
        ) =
        lock gate (fun () ->
            let mutable preview = Unchecked.defaultof<ConfirmedWorkspaceEdit>

            if not (previews.Remove(token.Value, &preview)) then
                invalid "The confirmation token is unknown or already consumed."
            elif clock.GetUtcNow() > preview.ExpiresAtUtc then
                invalid "The confirmation token expired."
            else
                let actual = currentRevision ()
                let plan = actions |> Seq.toArray

                if actual.Value <> request.ExpectedRevision.Value then
                    conflict
                        request.ExpectedRevision
                        actual
                        "The workspace revision changed before execution."
                else
                    match bind request plan with
                    | Error error -> invalid error
                    | Ok(digest, _, _) when digest <> preview.Digest ->
                        invalid "The request or executable action plan changed after preview."
                    | Ok(_, _, fingerprints) when fingerprints <> preview.Fingerprints ->
                        conflict
                            request.ExpectedRevision
                            actual
                            "An artifact changed after preview."
                    | Ok _ ->
                        let reversals = ResizeArray<string * (unit -> unit)>()
                        let cleanup = ResizeArray<string>()
                        let irreversible = ResizeArray<string>()
                        let relocationSources = ResizeArray<string * string * string>()
                        let mutable failure = None

                        let fingerprint path =
                            match ArtifactFiles.fingerprint path with
                            | Ok value -> value
                            | Error error -> invalidOp error

                        let verifyFingerprint expected path message =
                            if fingerprint path <> expected then
                                invalidOp message

                        let commitStaged stage destination =
                            if ArtifactFiles.exists destination then
                                let backup = ArtifactFiles.temporaryBeside destination "rollback"
                                cleanup.Add backup

                                let restore () =
                                    if not (ArtifactFiles.exists backup) then
                                        invalidOp $"Rollback artifact is missing: {backup}"

                                    if
                                        File.Exists backup
                                        && File.Exists destination
                                        && not (ArtifactFiles.isLink backup)
                                        && not (ArtifactFiles.isLink destination)
                                    then
                                        File.Replace(backup, destination, null, true)
                                    else
                                        let displaced =
                                            ArtifactFiles.temporaryBeside destination "displaced"

                                        cleanup.Add displaced

                                        if ArtifactFiles.exists destination then
                                            ArtifactFiles.move destination displaced

                                        try
                                            ArtifactFiles.move backup destination
                                        with _ ->
                                            if ArtifactFiles.exists displaced then
                                                ArtifactFiles.move displaced destination

                                            reraise ()

                                if
                                    File.Exists destination
                                    && not (ArtifactFiles.isLink destination)
                                then
                                    File.Replace(stage, destination, backup, true)
                                else
                                    ArtifactFiles.move destination backup

                                    try
                                        ArtifactFiles.move stage destination
                                    with _ ->
                                        ArtifactFiles.move backup destination
                                        reraise ()

                                $"restore {destination}", restore
                            else
                                ArtifactFiles.move stage destination

                                $"remove {destination}", fun () -> ArtifactFiles.remove destination

                        try
                            let projectDirectory =
                                request.Targets
                                |> Seq.tryHead
                                |> Option.map (fun target ->
                                    Path.GetDirectoryName target.Value
                                    |> Option.ofObj
                                    |> Option.defaultValue workspaceRoot.Value)
                                |> Option.defaultValue workspaceRoot.Value

                            match ProjectFolderActions.bind projectDirectory request with
                            | Error error -> invalidOp error
                            | Ok folderActions ->
                                for action in folderActions do
                                    cancellationToken.ThrowIfCancellationRequested()
                                    let applied = ProjectFolderActions.execute action

                                    reversals.Insert(
                                        0,
                                        ($"compensate folder action {action}",
                                         fun () -> ProjectFolderActions.compensate applied)
                                    )

                                    cancellationToken.ThrowIfCancellationRequested()

                            for action in plan do
                                cancellationToken.ThrowIfCancellationRequested()

                                for path in actionPaths action do
                                    match ArtifactFiles.canonicalNoFollow true path with
                                    | Error error -> invalidOp error
                                    | Ok _ -> ()

                                match action with
                                | WorkspaceEditAction.CreateDirectory path ->
                                    Directory.CreateDirectory path |> ignore

                                    reversals.Insert(
                                        0,
                                        ($"remove directory {path}",
                                         fun () ->
                                             if Directory.Exists path then
                                                 Directory.Delete path)
                                    )
                                | WorkspaceEditAction.ReplaceFile(destination, contents) ->
                                    let stage = ArtifactFiles.temporaryBeside destination "stage"
                                    cleanup.Add stage
                                    File.WriteAllBytes(stage, contents)
                                    let expected = fingerprint stage
                                    reversals.Insert(0, commitStaged stage destination)

                                    verifyFingerprint
                                        expected
                                        destination
                                        "The replaced artifact did not verify."
                                | WorkspaceEditAction.Rename(source, destination) ->
                                    let expected = fingerprint source

                                    let caseOnly =
                                        ArtifactFiles.isCaseOnlyRename source destination

                                    let temporary = ArtifactFiles.temporaryBeside source "rename"
                                    cleanup.Add temporary
                                    ArtifactFiles.move source temporary

                                    let _, restoreDestination =
                                        try
                                            commitStaged temporary destination
                                        with _ ->
                                            ArtifactFiles.move temporary source
                                            reraise ()

                                    reversals.Insert(
                                        0,
                                        ($"restore rename from {destination} to {source}",
                                         fun () ->
                                             ArtifactFiles.move destination source

                                             if not caseOnly then
                                                 restoreDestination ())
                                    )

                                    verifyFingerprint
                                        expected
                                        destination
                                        "The renamed artifact did not verify."

                                    if ArtifactFiles.exists source && not caseOnly then
                                        invalidOp "The renamed source artifact still exists."
                                | WorkspaceEditAction.Move(source, destination) when
                                    request.CommandId.Value = "project.relocate"
                                    && Directory.Exists source
                                    ->
                                    let expected = fingerprint source
                                    cleanup.Add destination

                                    ArtifactFiles.copyNoFollow source destination

                                    verifyFingerprint
                                        expected
                                        destination
                                        "The staged project directory did not verify."

                                    cleanup.Remove destination |> ignore

                                    reversals.Insert(
                                        0,
                                        ($"remove copied project directory {destination}",
                                         fun () ->
                                             verifyFingerprint
                                                 expected
                                                 destination
                                                 "The copied project directory changed before compensation."

                                             ArtifactFiles.remove destination

                                             if ArtifactFiles.exists destination then
                                                 invalidOp
                                                     "The copied project directory remained after compensation.")
                                    )

                                    relocationSources.Add(source, destination, expected)
                                | WorkspaceEditAction.Move(source, destination) ->
                                    let stage = ArtifactFiles.temporaryBeside destination "stage"
                                    cleanup.Add stage
                                    let mutable renamed = false

                                    try
                                        ArtifactFiles.move source stage
                                        renamed <- true
                                    with :? IOException ->
                                        ArtifactFiles.copyNoFollow source stage

                                        if
                                            ArtifactFiles.fingerprint source
                                            <> ArtifactFiles.fingerprint stage
                                        then
                                            invalidOp "The staged move did not verify."

                                    let _, restoreDestination =
                                        try
                                            commitStaged stage destination
                                        with _ ->
                                            if renamed && ArtifactFiles.exists stage then
                                                ArtifactFiles.move stage source

                                            reraise ()

                                    if renamed then
                                        reversals.Insert(
                                            0,
                                            ($"restore move from {destination} to {source}",
                                             fun () ->
                                                 ArtifactFiles.move destination source
                                                 restoreDestination ())
                                        )
                                    else
                                        try
                                            ArtifactFiles.remove source
                                        with ex ->
                                            irreversible.Add(
                                                $"remove incomplete source {source}; "
                                                + $"verified destination retained at {destination}"
                                            )

                                            raise ex

                                        reversals.Insert(
                                            0,
                                            ($"restore cross-volume move from {destination} "
                                             + $"to {source}",
                                             fun () ->
                                                 ArtifactFiles.remove source
                                                 ArtifactFiles.copyNoFollow destination source
                                                 restoreDestination ())
                                        )
                                | WorkspaceEditAction.Copy(source, destination) ->
                                    let stage = ArtifactFiles.temporaryBeside destination "stage"
                                    cleanup.Add stage
                                    let expected = fingerprint source
                                    ArtifactFiles.copyNoFollow source stage

                                    verifyFingerprint
                                        expected
                                        stage
                                        "The staged copy did not verify."

                                    let _, restoreDestination =
                                        try
                                            commitStaged stage destination
                                        with _ ->
                                            ArtifactFiles.remove stage
                                            reraise ()

                                    reversals.Insert(
                                        0,
                                        ($"remove copy {destination}",
                                         fun () ->
                                             ArtifactFiles.remove destination
                                             restoreDestination ())
                                    )

                                    verifyFingerprint
                                        expected
                                        destination
                                        "The copied artifact did not verify."
                                | WorkspaceEditAction.Delete(path, false, _)
                                | WorkspaceEditAction.Trash path ->
                                    match trash.MoveToTrash path with
                                    | Ok() -> irreversible.Add $"moved to trash: {path}"
                                    | Error error ->
                                        raise (
                                            WorkspaceEditFailed(
                                                unsupported $"Trash refused: {error.Message}"
                                            )
                                        )
                                | WorkspaceEditAction.Delete(path, true, recursive) ->
                                    ArtifactFiles.deletePermanent path recursive
                                    irreversible.Add $"permanently deleted: {path}"

                                cancellationToken.ThrowIfCancellationRequested()

                            for source, destination, expected in relocationSources do
                                cancellationToken.ThrowIfCancellationRequested()

                                let sourceExpected = fingerprint source
                                let destinationExpected = fingerprint destination

                                if
                                    sourceExpected <> destinationExpected
                                    || sourceExpected <> expected
                                then
                                    invalidOp
                                        "The copied project directory no longer matches its source."

                                let temporary = ArtifactFiles.temporaryBeside source "source"
                                cleanup.Add temporary
                                ArtifactFiles.move source temporary

                                reversals.Insert(
                                    0,
                                    ($"restore project source {source}",
                                     fun () ->
                                         verifyFingerprint
                                             destinationExpected
                                             destination
                                             "The copied project directory changed before source restoration."

                                         if ArtifactFiles.exists source then
                                             invalidOp
                                                 "The project source changed before restoration."

                                         ArtifactFiles.move temporary source

                                         verifyFingerprint
                                             sourceExpected
                                             source
                                             "The restored project source did not verify.")
                                )

                                cancellationToken.ThrowIfCancellationRequested()
                        with
                        | :? OperationCanceledException ->
                            failure <-
                                Some(
                                    Cancelled(
                                        WorkspaceOperationId.New(),
                                        diagnostic "cancelled" "The mutation was cancelled."
                                    )
                                )
                        | WorkspaceEditFailed typed -> failure <- Some typed
                        | ex -> failure <- Some(internalFailure ex.Message)

                        if failure.IsNone && not (tryReserveCommit ()) then
                            failure <-
                                Some(
                                    Cancelled(
                                        WorkspaceOperationId.New(),
                                        diagnostic "cancelled" "The mutation was cancelled."
                                    )
                                )

                        match failure with
                        | None ->
                            match cleanAll cleanup with
                            | [] -> Success Applied
                            | remaining -> partial remaining
                        | Some original ->
                            let remaining =
                                reverseAll reversals
                                @ (irreversible |> Seq.toList)
                                @ cleanAll cleanup

                            if remaining.IsEmpty then
                                Success(RolledBack original)
                            else
                                partial remaining)

    member this.Execute
        (
            request: WorkspaceEditPreviewRequest,
            actions: seq<WorkspaceEditAction>,
            token: WorkspaceEditConfirmation,
            cancellationToken: CancellationToken
        ) =
        this.ExecuteCore(request, actions, token, cancellationToken, fun () -> true)

    member internal this.ExecuteOperation
        (
            request: WorkspaceEditPreviewRequest,
            actions: seq<WorkspaceEditAction>,
            token: WorkspaceEditConfirmation,
            cancellationToken: CancellationToken,
            tryReserveCommit: unit -> bool
        ) =
        this.ExecuteCore(request, actions, token, cancellationToken, tryReserveCommit)

    static member CreateProduction
        (workspaceRoot: WorkspaceArtifactPath, currentRevision: unit -> WorkspaceRevision)
        =
        WorkspaceEditTransaction(
            workspaceRoot,
            TimeProvider.System,
            currentRevision,
            NativeArtifactTrash.CreateForCurrentUser()
        )
