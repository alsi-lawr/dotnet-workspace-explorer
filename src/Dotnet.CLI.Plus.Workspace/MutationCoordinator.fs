namespace Dotnet.CLI.Plus

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Threading
open Microsoft.VisualBasic.FileIO
open Dotnet.CLI.Plus.Core

exception private MutationFailed of WorkspaceFailure

module internal NativeTrash =
    type Freedesktop(dataHome: string) =
        interface TrashBackend with
            member _.MoveToTrash path =
                try
                    let root = Path.Combine(dataHome, "Trash")
                    let files = Directory.CreateDirectory(Path.Combine(root, "files")).FullName
                    let info = Directory.CreateDirectory(Path.Combine(root, "info")).FullName

                    if not (OperatingSystem.IsWindows()) then
                        let mode =
                            UnixFileMode.UserRead
                            ||| UnixFileMode.UserWrite
                            ||| UnixFileMode.UserExecute

                        [ root; files; info ]
                        |> List.iter (fun path -> File.SetUnixFileMode(path, mode))

                    let baseName =
                        Path.GetFileName path |> Option.ofObj |> Option.defaultValue "item"

                    let mutable name = baseName
                    let mutable suffix = 1

                    while MutationFiles.exists (Path.Combine(files, name))
                          || File.Exists(Path.Combine(info, $"{name}.trashinfo")) do
                        name <- $"{baseName}.{suffix}"
                        suffix <- suffix + 1

                    let metadata = Path.Combine(info, $"{name}.trashinfo")

                    let escaped =
                        Uri
                            .EscapeDataString(Path.GetFullPath path)
                            .Replace("%2F", "/", StringComparison.Ordinal)

                    let deleted = DateTime.Now.ToString "yyyy-MM-ddTHH:mm:ss"

                    use stream =
                        File.Open(metadata, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                    use writer = new StreamWriter(stream, UTF8Encoding false, leaveOpen = true)
                    writer.Write $"[Trash Info]\nPath={escaped}\nDeletionDate={deleted}\n"
                    writer.Flush()
                    stream.Flush true

                    try
                        MutationFiles.move path (Path.Combine(files, name))
                        Ok()
                    with ex ->
                        File.Delete metadata
                        Error { Message = ex.Message }
                with ex ->
                    Error { Message = ex.Message }

    type Windows() =
        interface TrashBackend with
            member _.MoveToTrash path =
                try
                    if File.Exists path then
                        FileSystem.DeleteFile(
                            path,
                            UIOption.OnlyErrorDialogs,
                            RecycleOption.SendToRecycleBin,
                            UICancelOption.ThrowException
                        )
                    elif Directory.Exists path then
                        FileSystem.DeleteDirectory(
                            path,
                            UIOption.OnlyErrorDialogs,
                            RecycleOption.SendToRecycleBin,
                            UICancelOption.ThrowException
                        )
                    else
                        raise (FileNotFoundException("The artifact does not exist.", path))

                    Ok()
                with ex ->
                    Error { Message = ex.Message }

    module private Mac =
        [<DllImport("/usr/lib/libobjc.A.dylib")>]
        extern IntPtr objc_getClass(string _name)

        [<DllImport("/usr/lib/libobjc.A.dylib")>]
        extern IntPtr sel_registerName(string _name)

        [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
        extern IntPtr send0(IntPtr _receiver, IntPtr _selector)

        [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
        extern IntPtr sendUtf8(
            IntPtr _receiver,
            IntPtr _selector,
            [<MarshalAs(UnmanagedType.LPUTF8Str)>] string _value
        )

        [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
        extern IntPtr sendPointer(IntPtr _receiver, IntPtr _selector, IntPtr _value)

        [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
        extern byte sendTrash(
            IntPtr _receiver,
            IntPtr _selector,
            IntPtr _url,
            IntPtr _result,
            IntPtr _error
        )

        let selector value = sel_registerName value

        let trash path =
            let manager = send0 (objc_getClass "NSFileManager", selector "defaultManager")

            let text =
                sendUtf8 (objc_getClass "NSString", selector "stringWithUTF8String:", path)

            let url = sendPointer (objc_getClass "NSURL", selector "fileURLWithPath:", text)

            sendTrash (
                manager,
                selector "trashItemAtURL:resultingItemURL:error:",
                url,
                IntPtr.Zero,
                IntPtr.Zero
            )
            <> 0uy

    type MacOS() =
        interface TrashBackend with
            member _.MoveToTrash path =
                try
                    if Mac.trash path then
                        Ok()
                    else
                        Error { Message = "The native macOS trash API refused the item." }
                with ex ->
                    Error { Message = ex.Message }

    let current () : TrashBackend =
        if OperatingSystem.IsWindows() then
            Windows()
        elif OperatingSystem.IsMacOS() then
            MacOS()
        else
            let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

            let data =
                match Environment.GetEnvironmentVariable "XDG_DATA_HOME" with
                | null
                | "" -> Path.Combine(home, ".local", "share")
                | value -> value

            Freedesktop data

type MutationTrash private () =
    static member CreateForCurrentUser() : TrashBackend = NativeTrash.current ()

type private BoundPreview =
    { Digest: string
      ExpiresAtUtc: DateTimeOffset
      Fingerprints: Map<string, string> }

type MutationCoordinator
    (
        workspaceRoot: WorkspaceArtifactPath,
        clock: TimeProvider,
        currentRevision: unit -> WorkspaceRevision,
        trash: TrashBackend
    ) =
    let gate = obj ()
    let previews = Dictionary<string, BoundPreview> StringComparer.Ordinal

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
            CanonicalBinary.writeSection writer "texts" values.Length

            for value in values do
                CanonicalBinary.writeValue writer value

            writer.Flush()
            $"texts:{Convert.ToHexString(stream.ToArray())}"

    let actionPaths =
        function
        | MutationAction.ReplaceFile(path, _)
        | MutationAction.Delete(path, _, _)
        | MutationAction.Trash path -> [ path ]
        | MutationAction.Rename(source, destination)
        | MutationAction.Move(source, destination) -> [ source; destination ]

    let bind request (actions: MutationAction array) =
        let workspaceRoot = MutationFiles.canonicalNoFollow false workspaceRoot.Value

        let roots =
            request.AuthorizedRoots
            |> Seq.map (fun path -> MutationFiles.canonicalNoFollow false path.Value)
            |> Seq.toArray

        let targets =
            request.Targets
            |> Seq.map (fun path -> MutationFiles.canonicalNoFollow true path.Value)
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
                MutationFiles.isUnder canonicalWorkspaceRoot path
                || canonicalRoots |> Array.exists (fun root -> MutationFiles.isUnder root path)

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
                    |> Seq.map (MutationFiles.canonicalNoFollow true)
                    |> Seq.toArray

            match folderActions, failure planPaths with
            | Error error, _ -> Error error
            | _, Some error -> Error error
            | Ok folderActions, None ->
                let canonicalPlanPaths = planPaths |> Array.choose Result.toOption

                if canonicalTargets |> Array.exists (authorized >> not) then
                    Error "Every target requires an explicit authorization root."
                elif
                    canonicalTargets
                    |> Array.exists (fun path ->
                        not (MutationFiles.isUnder canonicalWorkspaceRoot path))
                    && not (request.Intents.Contains MutationIntent.AccessExternalPath)
                then
                    Error "External paths require explicit external-path intent."
                elif
                    canonicalPlanPaths
                    |> Array.exists (fun path ->
                        not (authorized path && Array.contains path canonicalTargets))
                then
                    Error "Every action path must be an explicitly authorized target."
                else
                    let fingerprints =
                        canonicalTargets
                        |> Array.map (fun path ->
                            MutationFiles.fingerprint path |> Result.map (fun value -> path, value))

                    match failure fingerprints with
                    | Some error -> Error error
                    | None ->
                        use stream = new MemoryStream()
                        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
                        CanonicalBinary.writeSection writer "command" 1
                        CanonicalBinary.writeValue writer request.CommandId.Value
                        CanonicalBinary.writeSection writer "revision" 1
                        writer.Write request.ExpectedRevision.Value
                        CanonicalBinary.writeSection writer "workspace-root" 1
                        CanonicalBinary.writeValue writer canonicalWorkspaceRoot
                        CanonicalBinary.writeSection writer "targets" canonicalTargets.Length
                        canonicalTargets |> Array.iter (CanonicalBinary.writeValue writer)

                        let arguments =
                            request.Arguments.Values
                            |> Seq.sortBy _.ParameterId.Value
                            |> Seq.toArray

                        CanonicalBinary.writeSection writer "arguments" arguments.Length

                        for argument in arguments do
                            CanonicalBinary.writeValue writer argument.ParameterId.Value
                            CanonicalBinary.writeValue writer (argumentText argument)

                        let intents = request.Intents |> Seq.map int |> Seq.sort |> Seq.toArray
                        CanonicalBinary.writeSection writer "intents" intents.Length
                        intents |> Array.iter writer.Write
                        let orderedRoots = canonicalRoots |> Array.sort
                        CanonicalBinary.writeSection writer "roots" orderedRoots.Length
                        orderedRoots |> Array.iter (CanonicalBinary.writeValue writer)
                        CanonicalBinary.writeSection writer "actions" actions.Length
                        let mutable pathIndex = 0

                        let nextPath () =
                            let path = canonicalPlanPaths[pathIndex]
                            pathIndex <- pathIndex + 1
                            path

                        for action in actions do
                            match action with
                            | MutationAction.ReplaceFile(_path, contents) ->
                                CanonicalBinary.writeValue writer "replace"
                                CanonicalBinary.writeValue writer (nextPath ())

                                CanonicalBinary.writeValue
                                    writer
                                    (SHA256.HashData contents |> Convert.ToHexString)
                            | MutationAction.Rename(_source, _destination) ->
                                CanonicalBinary.writeValue writer "rename"
                                CanonicalBinary.writeValue writer (nextPath ())
                                CanonicalBinary.writeValue writer (nextPath ())
                            | MutationAction.Move(_source, _destination) ->
                                CanonicalBinary.writeValue writer "move"
                                CanonicalBinary.writeValue writer (nextPath ())
                                CanonicalBinary.writeValue writer (nextPath ())
                            | MutationAction.Delete(_path, permanent, recursive) ->
                                CanonicalBinary.writeValue writer "delete"
                                CanonicalBinary.writeValue writer (nextPath ())
                                writer.Write permanent
                                writer.Write recursive
                            | MutationAction.Trash _path ->
                                CanonicalBinary.writeValue writer "trash"
                                CanonicalBinary.writeValue writer (nextPath ())

                        CanonicalBinary.writeSection writer "folder-actions" folderActions.Length
                        folderActions |> Array.iter (ProjectFolderActions.writeDigest writer)
                        writer.Flush()

                        Ok(
                            SHA256.HashData(stream.ToArray()) |> Convert.ToHexString,
                            canonicalTargets,
                            fingerprints |> Array.choose Result.toOption |> Map.ofArray
                        )

    let validate (intents: ImmutableHashSet<MutationIntent>) (actions: MutationAction array) =
        let destinations = HashSet<string> StringComparer.Ordinal

        let repeatedDestination path =
            destinations.Add(MutationFiles.identity path) |> not

        let irreversible =
            function
            | MutationAction.Trash _
            | MutationAction.Delete _ -> true
            | _ -> false

        let trailing =
            actions |> Array.skipWhile (irreversible >> not) |> Array.forall irreversible

        trailing
        && actions
           |> Array.forall (function
               | MutationAction.ReplaceFile(destination, _) ->
                   not (MutationFiles.exists destination || repeatedDestination destination)
                   || intents.Contains MutationIntent.Overwrite
               | MutationAction.Rename(source, destination) ->
                   let repeated = repeatedDestination destination

                   MutationFiles.exists source
                   && (not (MutationFiles.exists destination)
                       || MutationFiles.isCaseOnlyRename source destination
                       || intents.Contains MutationIntent.Overwrite)
                   && (not repeated || intents.Contains MutationIntent.Overwrite)
               | MutationAction.Move(source, destination) ->
                   let repeated = repeatedDestination destination

                   MutationFiles.exists source
                   && (not (MutationFiles.exists destination)
                       || intents.Contains MutationIntent.Overwrite)
                   && (not repeated || intents.Contains MutationIntent.Overwrite)
               | MutationAction.Delete(path, permanent, recursive) ->
                   MutationFiles.exists path
                   && (not permanent || intents.Contains MutationIntent.PermanentDelete)
                   && (not recursive || intents.Contains MutationIntent.RecursiveDelete)
                   && (not permanent || recursive || not (MutationFiles.nonEmptyDirectory path))
               | MutationAction.Trash path -> MutationFiles.exists path)

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
                MutationFiles.remove path

                if MutationFiles.exists path then
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

    member _.Prepare(request: MutationPreviewRequest, actions: seq<MutationAction>) =
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
                        { Confirmation = MutationConfirmationToken.Create token
                          ExpiresAtUtc = expiry })

    member private _.ExecuteCore
        (
            request: MutationPreviewRequest,
            actions: seq<MutationAction>,
            token: MutationConfirmationToken,
            cancellationToken: CancellationToken,
            tryReserveCommit: unit -> bool
        ) =
        lock gate (fun () ->
            let mutable preview = Unchecked.defaultof<BoundPreview>

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
                            match MutationFiles.fingerprint path with
                            | Ok value -> value
                            | Error error -> invalidOp error

                        let verifyFingerprint expected path message =
                            if fingerprint path <> expected then
                                invalidOp message

                        let commitStaged stage destination =
                            if MutationFiles.exists destination then
                                let backup = MutationFiles.temporaryBeside destination "rollback"
                                cleanup.Add backup

                                let restore () =
                                    if not (MutationFiles.exists backup) then
                                        invalidOp $"Rollback artifact is missing: {backup}"

                                    if
                                        File.Exists backup
                                        && File.Exists destination
                                        && not (MutationFiles.isLink backup)
                                        && not (MutationFiles.isLink destination)
                                    then
                                        File.Replace(backup, destination, null, true)
                                    else
                                        let displaced =
                                            MutationFiles.temporaryBeside destination "displaced"

                                        cleanup.Add displaced

                                        if MutationFiles.exists destination then
                                            MutationFiles.move destination displaced

                                        try
                                            MutationFiles.move backup destination
                                        with _ ->
                                            if MutationFiles.exists displaced then
                                                MutationFiles.move displaced destination

                                            reraise ()

                                if
                                    File.Exists destination
                                    && not (MutationFiles.isLink destination)
                                then
                                    File.Replace(stage, destination, backup, true)
                                else
                                    MutationFiles.move destination backup

                                    try
                                        MutationFiles.move stage destination
                                    with _ ->
                                        MutationFiles.move backup destination
                                        reraise ()

                                $"restore {destination}", restore
                            else
                                MutationFiles.move stage destination

                                $"remove {destination}", fun () -> MutationFiles.remove destination

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
                                    match MutationFiles.canonicalNoFollow true path with
                                    | Error error -> invalidOp error
                                    | Ok _ -> ()

                                match action with
                                | MutationAction.ReplaceFile(destination, contents) ->
                                    let stage = MutationFiles.temporaryBeside destination "stage"
                                    cleanup.Add stage
                                    File.WriteAllBytes(stage, contents)
                                    let expected = fingerprint stage
                                    reversals.Insert(0, commitStaged stage destination)

                                    verifyFingerprint
                                        expected
                                        destination
                                        "The replaced artifact did not verify."
                                | MutationAction.Rename(source, destination) ->
                                    let expected = fingerprint source

                                    let caseOnly =
                                        MutationFiles.isCaseOnlyRename source destination

                                    let temporary = MutationFiles.temporaryBeside source "rename"
                                    cleanup.Add temporary
                                    MutationFiles.move source temporary

                                    let _, restoreDestination =
                                        try
                                            commitStaged temporary destination
                                        with _ ->
                                            MutationFiles.move temporary source
                                            reraise ()

                                    reversals.Insert(
                                        0,
                                        ($"restore rename from {destination} to {source}",
                                         fun () ->
                                             MutationFiles.move destination source

                                             if not caseOnly then
                                                 restoreDestination ())
                                    )

                                    verifyFingerprint
                                        expected
                                        destination
                                        "The renamed artifact did not verify."

                                    if MutationFiles.exists source && not caseOnly then
                                        invalidOp "The renamed source artifact still exists."
                                | MutationAction.Move(source, destination) when
                                    request.CommandId.Value = "project.physical-move"
                                    && Directory.Exists source
                                    ->
                                    let expected = fingerprint source
                                    cleanup.Add destination

                                    MutationFiles.copyNoFollow source destination

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

                                             MutationFiles.remove destination

                                             if MutationFiles.exists destination then
                                                 invalidOp
                                                     "The copied project directory remained after compensation.")
                                    )

                                    relocationSources.Add(source, destination, expected)
                                | MutationAction.Move(source, destination) ->
                                    let stage = MutationFiles.temporaryBeside destination "stage"
                                    cleanup.Add stage
                                    let mutable renamed = false

                                    try
                                        MutationFiles.move source stage
                                        renamed <- true
                                    with :? IOException ->
                                        MutationFiles.copyNoFollow source stage

                                        if
                                            MutationFiles.fingerprint source
                                            <> MutationFiles.fingerprint stage
                                        then
                                            invalidOp "The staged move did not verify."

                                    let _, restoreDestination =
                                        try
                                            commitStaged stage destination
                                        with _ ->
                                            if renamed && MutationFiles.exists stage then
                                                MutationFiles.move stage source

                                            reraise ()

                                    if renamed then
                                        reversals.Insert(
                                            0,
                                            ($"restore move from {destination} to {source}",
                                             fun () ->
                                                 MutationFiles.move destination source
                                                 restoreDestination ())
                                        )
                                    else
                                        try
                                            MutationFiles.remove source
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
                                                 MutationFiles.remove source
                                                 MutationFiles.copyNoFollow destination source
                                                 restoreDestination ())
                                        )
                                | MutationAction.Delete(path, false, _)
                                | MutationAction.Trash path ->
                                    match trash.MoveToTrash path with
                                    | Ok() -> irreversible.Add $"moved to trash: {path}"
                                    | Error error ->
                                        raise (
                                            MutationFailed(
                                                unsupported $"Trash refused: {error.Message}"
                                            )
                                        )
                                | MutationAction.Delete(path, true, recursive) ->
                                    MutationFiles.deletePermanent path recursive
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

                                let temporary = MutationFiles.temporaryBeside source "source"
                                cleanup.Add temporary
                                MutationFiles.move source temporary

                                reversals.Insert(
                                    0,
                                    ($"restore project source {source}",
                                     fun () ->
                                         verifyFingerprint
                                             destinationExpected
                                             destination
                                             "The copied project directory changed before source restoration."

                                         if MutationFiles.exists source then
                                             invalidOp
                                                 "The project source changed before restoration."

                                         MutationFiles.move temporary source

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
                                        OperationId.New(),
                                        diagnostic "cancelled" "The mutation was cancelled."
                                    )
                                )
                        | MutationFailed typed -> failure <- Some typed
                        | ex -> failure <- Some(internalFailure ex.Message)

                        if failure.IsNone && not (tryReserveCommit ()) then
                            failure <-
                                Some(
                                    Cancelled(
                                        OperationId.New(),
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
            request: MutationPreviewRequest,
            actions: seq<MutationAction>,
            token: MutationConfirmationToken,
            cancellationToken: CancellationToken
        ) =
        this.ExecuteCore(request, actions, token, cancellationToken, fun () -> true)

    member internal this.ExecuteOperation
        (
            request: MutationPreviewRequest,
            actions: seq<MutationAction>,
            token: MutationConfirmationToken,
            cancellationToken: CancellationToken,
            tryReserveCommit: unit -> bool
        ) =
        this.ExecuteCore(request, actions, token, cancellationToken, tryReserveCommit)

    static member CreateProduction
        (workspaceRoot: WorkspaceArtifactPath, currentRevision: unit -> WorkspaceRevision)
        =
        MutationCoordinator(
            workspaceRoot,
            TimeProvider.System,
            currentRevision,
            MutationTrash.CreateForCurrentUser()
        )
