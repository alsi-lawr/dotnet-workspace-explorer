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

[<RequireQualifiedAccess>]
type MutationAction =
    | ReplaceFile of destination: string * contents: byte array
    | Rename of source: string * destination: string
    | Move of source: string * destination: string
    | Delete of path: string * permanent: bool * recursive: bool
    | Trash of path: string

type TrashFailure = { Message: string }

type TrashBackend =
    abstract MoveToTrash: string -> Result<unit, TrashFailure>

exception private MutationFailed of WorkspaceFailure

module private CanonicalBinary =
    let writeValue (writer: BinaryWriter) (value: string) =
        let bytes = Encoding.UTF8.GetBytes value
        writer.Write bytes.Length
        writer.Write bytes

    let writeSection (writer: BinaryWriter) tag (count: int) =
        writeValue writer tag
        writer.Write count

module internal MutationFiles =
    let linkTarget path =
        let file = FileInfo path :> FileSystemInfo
        let directory = DirectoryInfo path :> FileSystemInfo

        [ file.LinkTarget; directory.LinkTarget ]
        |> List.choose Option.ofObj
        |> List.tryHead

    let isLink path = linkTarget path |> Option.isSome

    let exists path =
        isLink path || File.Exists path || Directory.Exists path

    let canonicalNoFollow allowTerminalLink path =
        let full = Path.GetFullPath path

        match Path.GetPathRoot(full) |> Option.ofObj with
        | None -> Error "The path has no filesystem root."
        | Some root ->
            let mutable current = root
            let mutable linked = false

            let segments =
                Path
                    .GetRelativePath(root, full)
                    .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)

            for index, segment in Array.indexed segments do
                current <- Path.Combine(current, segment)

                if isLink current && (not allowTerminalLink || index <> segments.Length - 1) then
                    linked <- true

            if linked then
                Error "A symbolic link component is not allowed."
            else
                Ok full

    let isUnder root path =
        let relative = Path.GetRelativePath(root, path)

        relative <> ".."
        && not (relative.StartsWith($"..{Path.DirectorySeparatorChar}"))
        && not (Path.IsPathRooted relative)

    let rec private fingerprintAt allowLink path =
        if isLink path then
            if allowLink then
                let target = linkTarget path |> Option.defaultValue String.Empty
                Ok($"l:{SHA256.HashData(Encoding.UTF8.GetBytes target) |> Convert.ToHexString}")
            else
                Error "A symbolic link within a directory cannot be fingerprinted."
        elif File.Exists path then
            use stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            Ok($"f:{(FileInfo path).Length}:{SHA256.HashData(stream) |> Convert.ToHexString}")
        elif Directory.Exists path then
            let children =
                Directory.EnumerateFileSystemEntries(path)
                |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
                |> Seq.map (fun child ->
                    fingerprintAt false child
                    |> Result.map (fun value ->
                        Path.GetFileName(child) |> Option.ofObj |> Option.defaultValue String.Empty, value))
                |> Seq.toArray

            match
                children
                |> Array.tryPick (function
                    | Error error -> Some error
                    | Ok _ -> None)
            with
            | Some error -> Error error
            | None ->
                use stream = new MemoryStream()
                use writer = new BinaryWriter(stream, Encoding.UTF8, true)
                CanonicalBinary.writeSection writer "directory" children.Length

                for name, value in children |> Array.choose Result.toOption do
                    CanonicalBinary.writeValue writer name
                    CanonicalBinary.writeValue writer value

                writer.Flush()
                Ok($"d:{SHA256.HashData(stream.ToArray()) |> Convert.ToHexString}")
        else
            Ok "missing"

    let fingerprint path = fingerprintAt true path

    let rec copyNoFollow source destination =
        if isLink source then
            invalidOp "A symbolic link cannot be copied."
        elif File.Exists source then
            File.Copy(source, destination)
        elif Directory.Exists source then
            Directory.CreateDirectory(destination) |> ignore

            for child in
                Directory.EnumerateFileSystemEntries(source)
                |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right)) do
                let name =
                    Path.GetFileName(child) |> Option.ofObj |> Option.defaultValue String.Empty

                copyNoFollow child (Path.Combine(destination, name))
        else
            raise (FileNotFoundException("The source artifact does not exist.", source))

    let move source destination =
        if isLink source || File.Exists source then
            File.Move(source, destination)
        else
            Directory.Move(source, destination)

    let remove path =
        if isLink path || File.Exists path then
            File.Delete path
        elif Directory.Exists path then
            Directory.Delete(path, true)

    let deletePermanent path recursive =
        if isLink path || File.Exists path then
            File.Delete path
        elif Directory.Exists path then
            Directory.Delete(path, recursive)

    let nonEmptyDirectory path =
        Directory.Exists path
        && not (isLink path)
        && Directory.EnumerateFileSystemEntries(path) |> Seq.isEmpty |> not

    let isCaseOnlyRename source destination =
        try
            let sourcePath = Path.GetFullPath source
            let destinationPath = Path.GetFullPath destination

            not (String.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
            && String.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase)
            && HostFileSystemCaseDetector.DetectFromExistingPath(sourcePath) = HostFileSystemCaseSemantics.Insensitive
        with _ ->
            false

    let identity path =
        let full = Path.GetFullPath path

        let rec nearestExisting candidate =
            if File.Exists candidate || Directory.Exists candidate then
                candidate
            else
                match Path.GetDirectoryName(candidate) |> Option.ofObj with
                | Some parent when parent <> candidate -> nearestExisting parent
                | _ -> candidate

        match HostFileSystemCaseDetector.DetectFromExistingPath(nearestExisting full) with
        | HostFileSystemCaseSemantics.Insensitive -> full.ToUpperInvariant()
        | _ -> full

    let temporaryBeside (path: string) (kind: string) =
        let directory =
            Path.GetDirectoryName(path) |> Option.ofObj |> Option.defaultValue "."

        let name = Path.GetFileName(path) |> Option.ofObj |> Option.defaultValue "artifact"
        Path.Combine(directory, $".{name}.dotnet-plus-{kind}-{Guid.NewGuid():N}")

module internal NativeTrash =
    type Freedesktop(dataHome: string) =
        interface TrashBackend with
            member _.MoveToTrash(path) =
                try
                    let root = Path.Combine(dataHome, "Trash")
                    let files = Directory.CreateDirectory(Path.Combine(root, "files")).FullName
                    let info = Directory.CreateDirectory(Path.Combine(root, "info")).FullName

                    if not (OperatingSystem.IsWindows()) then
                        let mode =
                            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

                        [ root; files; info ]
                        |> List.iter (fun path -> File.SetUnixFileMode(path, mode))

                    let baseName = Path.GetFileName(path) |> Option.ofObj |> Option.defaultValue "item"
                    let mutable name = baseName
                    let mutable suffix = 1

                    while MutationFiles.exists (Path.Combine(files, name))
                          || File.Exists(Path.Combine(info, $"{name}.trashinfo")) do
                        name <- $"{baseName}.{suffix}"
                        suffix <- suffix + 1

                    let metadata = Path.Combine(info, $"{name}.trashinfo")

                    let escaped =
                        Uri.EscapeDataString(Path.GetFullPath path).Replace("%2F", "/", StringComparison.Ordinal)

                    let deleted = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")

                    use stream =
                        File.Open(metadata, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                    use writer = new StreamWriter(stream, UTF8Encoding(false), leaveOpen = true)
                    writer.Write($"[Trash Info]\nPath={escaped}\nDeletionDate={deleted}\n")
                    writer.Flush()
                    stream.Flush(true)

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
            member _.MoveToTrash(path) =
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
        extern IntPtr objc_getClass(string name)

        [<DllImport("/usr/lib/libobjc.A.dylib")>]
        extern IntPtr sel_registerName(string name)

        [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
        extern IntPtr send0(IntPtr receiver, IntPtr selector)

        [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
        extern IntPtr sendUtf8(IntPtr receiver, IntPtr selector, [<MarshalAs(UnmanagedType.LPUTF8Str)>] string value)

        [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
        extern IntPtr sendPointer(IntPtr receiver, IntPtr selector, IntPtr value)

        [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
        extern byte sendTrash(IntPtr receiver, IntPtr selector, IntPtr url, IntPtr result, IntPtr error)

        let selector value = sel_registerName value

        let trash path =
            let manager = send0 (objc_getClass "NSFileManager", selector "defaultManager")

            let text =
                sendUtf8 (objc_getClass "NSString", selector "stringWithUTF8String:", path)

            let url = sendPointer (objc_getClass "NSURL", selector "fileURLWithPath:", text)

            sendTrash (manager, selector "trashItemAtURL:resultingItemURL:error:", url, IntPtr.Zero, IntPtr.Zero)
            <> 0uy

    type MacOS() =
        interface TrashBackend with
            member _.MoveToTrash(path) =
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

            Freedesktop(data)

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
    let previews = Dictionary<string, BoundPreview>(StringComparer.Ordinal)

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
        UnsupportedCapability(WorkspaceCapabilityId.Write, diagnostic "unsupported_capability" message)

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

            let planPaths =
                actions
                |> Seq.collect actionPaths
                |> Seq.map (MutationFiles.canonicalNoFollow true)
                |> Seq.toArray

            match failure planPaths with
            | Some error -> Error error
            | None ->
                let canonicalPlanPaths = planPaths |> Array.choose Result.toOption

                if canonicalTargets |> Array.exists (authorized >> not) then
                    Error "Every target requires an explicit authorization root."
                elif
                    canonicalTargets
                    |> Array.exists (fun path -> not (MutationFiles.isUnder canonicalWorkspaceRoot path))
                    && not (request.Intents.Contains MutationIntent.AccessExternalPath)
                then
                    Error "External paths require explicit external-path intent."
                elif
                    canonicalPlanPaths
                    |> Array.exists (fun path -> not (authorized path && Array.contains path canonicalTargets))
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
                            request.Arguments.Values |> Seq.sortBy _.ParameterId.Value |> Seq.toArray

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
                            | MutationAction.ReplaceFile(path, contents) ->
                                CanonicalBinary.writeValue writer "replace"
                                CanonicalBinary.writeValue writer (nextPath ())

                                CanonicalBinary.writeValue writer (SHA256.HashData(contents) |> Convert.ToHexString)
                            | MutationAction.Rename(source, destination) ->
                                CanonicalBinary.writeValue writer "rename"
                                CanonicalBinary.writeValue writer (nextPath ())
                                CanonicalBinary.writeValue writer (nextPath ())
                            | MutationAction.Move(source, destination) ->
                                CanonicalBinary.writeValue writer "move"
                                CanonicalBinary.writeValue writer (nextPath ())
                                CanonicalBinary.writeValue writer (nextPath ())
                            | MutationAction.Delete(path, permanent, recursive) ->
                                CanonicalBinary.writeValue writer "delete"
                                CanonicalBinary.writeValue writer (nextPath ())
                                writer.Write permanent
                                writer.Write recursive
                            | MutationAction.Trash path ->
                                CanonicalBinary.writeValue writer "trash"
                                CanonicalBinary.writeValue writer (nextPath ())

                        writer.Flush()

                        Ok(
                            SHA256.HashData(stream.ToArray()) |> Convert.ToHexString,
                            canonicalTargets,
                            fingerprints |> Array.choose Result.toOption |> Map.ofArray
                        )

    let validate (intents: ImmutableHashSet<MutationIntent>) (actions: MutationAction array) =
        let destinations = HashSet<string>(StringComparer.Ordinal)

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
                remaining.Add($"{description}: {ex.Message}")

        remaining |> Seq.toList

    let cleanAll paths =
        let remaining = ResizeArray<string>()

        for path in paths do
            try
                MutationFiles.remove path

                if MutationFiles.exists path then
                    remaining.Add($"remove temporary artifact: {path}")
            with ex ->
                remaining.Add($"remove temporary artifact {path}: {ex.Message}")

        remaining |> Seq.toList

    let partial remaining =
        let detail = String.concat "; " remaining

        Failure(
            PartialRecoveryRequired(detail, diagnostic "partial_recovery_required" $"Compensation incomplete: {detail}")
        )

    member _.Prepare(request: MutationPreviewRequest, actions: seq<MutationAction>) =
        lock gate (fun () ->
            let actual = currentRevision ()

            if actual.Value <> request.ExpectedRevision.Value then
                conflict request.ExpectedRevision actual "The workspace revision changed before preview."
            else
                let plan = actions |> Seq.toArray

                match bind request plan with
                | Error error -> invalid error
                | Ok(digest, _, fingerprints) when not (validate request.Intents plan) ->
                    invalid "The action plan lacks a required intent or valid irreversible ordering."
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

    member _.Execute
        (
            request: MutationPreviewRequest,
            actions: seq<MutationAction>,
            token: MutationConfirmationToken,
            cancellationToken: CancellationToken
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
                    conflict request.ExpectedRevision actual "The workspace revision changed before execution."
                else
                    match bind request plan with
                    | Error error -> invalid error
                    | Ok(digest, _, _) when digest <> preview.Digest ->
                        invalid "The request or executable action plan changed after preview."
                    | Ok(_, _, fingerprints) when fingerprints <> preview.Fingerprints ->
                        conflict request.ExpectedRevision actual "An artifact changed after preview."
                    | Ok _ ->
                        let reversals = ResizeArray<string * (unit -> unit)>()
                        let cleanup = ResizeArray<string>()
                        let irreversible = ResizeArray<string>()
                        let mutable failure = None

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
                                        let displaced = MutationFiles.temporaryBeside destination "displaced"
                                        cleanup.Add displaced

                                        if MutationFiles.exists destination then
                                            MutationFiles.move destination displaced

                                        try
                                            MutationFiles.move backup destination
                                        with _ ->
                                            if MutationFiles.exists displaced then
                                                MutationFiles.move displaced destination

                                            reraise ()

                                if File.Exists destination && not (MutationFiles.isLink destination) then
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
                                    reversals.Insert(0, commitStaged stage destination)
                                | MutationAction.Rename(source, destination) ->
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
                                             restoreDestination ())
                                    )
                                | MutationAction.Move(source, destination) ->
                                    let stage = MutationFiles.temporaryBeside destination "stage"
                                    cleanup.Add stage
                                    let mutable renamed = false

                                    try
                                        MutationFiles.move source stage
                                        renamed <- true
                                    with :? IOException ->
                                        MutationFiles.copyNoFollow source stage

                                        if MutationFiles.fingerprint source <> MutationFiles.fingerprint stage then
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
                                                $"remove incomplete source {source}; verified destination retained at {destination}"
                                            )

                                            raise ex

                                        reversals.Insert(
                                            0,
                                            ($"restore cross-volume move from {destination} to {source}",
                                             fun () ->
                                                 MutationFiles.remove source
                                                 MutationFiles.copyNoFollow destination source
                                                 restoreDestination ())
                                        )
                                | MutationAction.Delete(path, false, _)
                                | MutationAction.Trash path ->
                                    match trash.MoveToTrash path with
                                    | Ok() -> irreversible.Add($"moved to trash: {path}")
                                    | Error error ->
                                        raise (MutationFailed(unsupported $"Trash refused: {error.Message}"))
                                | MutationAction.Delete(path, true, recursive) ->
                                    MutationFiles.deletePermanent path recursive
                                    irreversible.Add($"permanently deleted: {path}")

                                cancellationToken.ThrowIfCancellationRequested()
                        with
                        | :? OperationCanceledException ->
                            failure <-
                                Some(
                                    Cancelled(OperationId.New(), diagnostic "cancelled" "The mutation was cancelled.")
                                )
                        | MutationFailed typed -> failure <- Some typed
                        | ex -> failure <- Some(internalFailure ex.Message)

                        match failure with
                        | None ->
                            match cleanAll cleanup with
                            | [] -> Success Applied
                            | remaining -> partial remaining
                        | Some original ->
                            let remaining =
                                reverseAll reversals @ (irreversible |> Seq.toList) @ cleanAll cleanup

                            if remaining.IsEmpty then
                                Success(RolledBack original)
                            else
                                partial remaining)

    static member CreateProduction(workspaceRoot: WorkspaceArtifactPath, currentRevision: unit -> WorkspaceRevision) =
        MutationCoordinator(workspaceRoot, TimeProvider.System, currentRevision, MutationTrash.CreateForCurrentUser())
