namespace Dotnet.CLI.Plus

open System
open System.Collections.Concurrent
open System.Collections.Immutable
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.VisualBasic.FileIO
open Dotnet.CLI.Plus.Core

[<RequireQualifiedAccess>]
type MutationAction =
    | ReplaceFile of destination: string * contents: byte array
    | Move of source: string * destination: string
    | Delete of path: string * permanent: bool * recursive: bool
    | Trash of path: string

type MutationClock =
    abstract UtcNow: DateTimeOffset

type SystemMutationClock() =
    interface MutationClock with
        member _.UtcNow = DateTimeOffset.UtcNow

type TrashFailure = { Message: string }

type TrashBackend =
    abstract MoveToTrash: string -> Result<unit, TrashFailure>

module internal MutationPaths =
    let stateRoot () =
        match Environment.GetEnvironmentVariable "DOTNET_PLUS_STATE_ROOT" with
        | null
        | "" ->
            let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

            if OperatingSystem.IsWindows() then
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "dotnet-cli-plus",
                    "transactions"
                )
            elif OperatingSystem.IsMacOS() then
                Path.Combine(home, "Library", "Application Support", "dotnet-cli-plus", "transactions")
            else
                let state =
                    match Environment.GetEnvironmentVariable "XDG_STATE_HOME" with
                    | null
                    | "" -> Path.Combine(home, ".local", "state")
                    | value -> value

                Path.Combine(state, "dotnet-cli-plus", "transactions")
        | value -> Path.GetFullPath value

    let ensurePrivateDirectory path =
        Directory.CreateDirectory path |> ignore

        if not (OperatingSystem.IsWindows()) then
            try
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                )
            with :? PlatformNotSupportedException ->
                ()

    let exists path =
        File.Exists path || Directory.Exists path

    let isLink path =
        let file = FileInfo path :> FileSystemInfo
        let directory = DirectoryInfo path :> FileSystemInfo

        (file.Exists && not (isNull file.LinkTarget))
        || (directory.Exists && not (isNull directory.LinkTarget))

    let noFollow path =
        let full = Path.GetFullPath path

        match Path.GetPathRoot(full) |> Option.ofObj with
        | None -> Error "The path has no filesystem root."
        | Some root ->
            let mutable current = root
            let mutable linked = false

            for segment in
                Path
                    .GetRelativePath(root, full)
                    .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries) do
                current <- Path.Combine(current, segment)

                if exists current && isLink current then
                    linked <- true

            if linked then
                Error "A symbolic link cannot be traversed."
            else
                Ok full

    let isUnder root path =
        let relative = Path.GetRelativePath(root, path)

        relative <> ".."
        && not (relative.StartsWith($"..{Path.DirectorySeparatorChar}"))
        && not (Path.IsPathRooted relative)

    let rec fingerprint path =
        if isLink path then
            Error "A symbolic link cannot be fingerprinted."
        elif File.Exists path then
            use stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            Ok($"f:{(new FileInfo(path)).Length}:{SHA256.HashData(stream) |> Convert.ToHexString}")
        elif Directory.Exists path then
            let children =
                Directory.EnumerateFileSystemEntries path
                |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
                |> Seq.map (fun child ->
                    fingerprint child
                    |> Result.map (fun value -> $"{Path.GetFileName(child)}:{value}"))
                |> Seq.toArray

            match
                children
                |> Array.tryPick (function
                    | Error value -> Some value
                    | Ok _ -> None)
            with
            | Some value -> Error value
            | None ->
                let text = children |> Array.choose Result.toOption |> String.concat "\n"
                Ok($"d:{SHA256.HashData(Encoding.UTF8.GetBytes(text)) |> Convert.ToHexString}")
        else
            Ok "missing"

    let rec copyTree source destination =
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

                copyTree child (Path.Combine(destination, name))
        else
            raise (FileNotFoundException("The source artifact does not exist.", source))

    let moveEntry source destination =
        if File.Exists source then
            File.Move(source, destination)
        else
            Directory.Move(source, destination)

    let removeEntry path =
        if File.Exists path then
            File.Delete path
        elif Directory.Exists path then
            Directory.Delete(path, true)

    let private name (path: string) =
        Path.GetFileName(path) |> Option.ofObj |> Option.defaultValue "artifact"

    let stageFor (destination: string) (id: string) =
        Path.Combine(
            Path.GetDirectoryName(destination) |> Option.ofObj |> Option.defaultValue ".",
            $".{name destination}.dotnet-plus-stage-{id}"
        )

    let backupFor (destination: string) (id: string) =
        Path.Combine(
            Path.GetDirectoryName(destination) |> Option.ofObj |> Option.defaultValue ".",
            $".{name destination}.dotnet-plus-backup-{id}"
        )

    let ownedSidecar (destination: string) (kind: string) (path: string) =
        let expected =
            Path.Combine(
                Path.GetDirectoryName(destination) |> Option.ofObj |> Option.defaultValue ".",
                $".{name destination}.dotnet-plus-{kind}-"
            )

        let fileName =
            Path.GetFileName(path) |> Option.ofObj |> Option.defaultValue String.Empty

        let prefix =
            Path.GetFileName(expected) |> Option.ofObj |> Option.defaultValue String.Empty

        String.Equals(Path.GetDirectoryName(destination), Path.GetDirectoryName(path), StringComparison.Ordinal)
        && fileName.StartsWith(prefix, StringComparison.Ordinal)

module internal NativeTrash =
    type Freedesktop(dataHome: string) =
        interface TrashBackend with
            member _.MoveToTrash(path) =
                try
                    let root = Path.Combine(dataHome, "Trash")
                    let files = Path.Combine(root, "files")
                    let info = Path.Combine(root, "info")
                    MutationPaths.ensurePrivateDirectory files
                    MutationPaths.ensurePrivateDirectory info
                    let baseName = Path.GetFileName(path) |> Option.ofObj |> Option.defaultValue "item"
                    let mutable candidate = baseName
                    let mutable suffix = 1

                    while MutationPaths.exists (Path.Combine(files, candidate))
                          || File.Exists(Path.Combine(info, $"{candidate}.trashinfo")) do
                        candidate <- $"{baseName}.{suffix}"
                        suffix <- suffix + 1

                    let metadata = Path.Combine(info, $"{candidate}.trashinfo")

                    let original =
                        Uri.EscapeDataString(Path.GetFullPath(path)).Replace("%2F", "/", StringComparison.Ordinal)

                    use stream =
                        File.Open(metadata, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                    use writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen = true)
                    let deleted = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss")
                    writer.Write($"[Trash Info]\nPath={original}\nDeletionDate={deleted}\n")
                    writer.Flush()
                    stream.Flush(true)

                    try
                        let destination = Path.Combine(files, candidate)

                        if File.Exists path then
                            File.Move(path, destination)
                        else
                            Directory.Move(path, destination)

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
                        invalidArg (nameof path) "The item does not exist."

                    Ok()
                with ex ->
                    Error { Message = ex.Message }

    module private MacNative =
        [<DllImport("/usr/lib/libobjc.A.dylib")>]
        extern IntPtr objc_getClass(string name)

        [<DllImport("/usr/lib/libobjc.A.dylib")>]
        extern IntPtr sel_registerName(string name)

        [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
        extern IntPtr sendNoArgument(IntPtr receiver, IntPtr selector)

        [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
        extern IntPtr sendString(IntPtr receiver, IntPtr selector, [<MarshalAs(UnmanagedType.LPUTF8Str)>] string value)

        [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
        extern byte trash(IntPtr receiver, IntPtr selector, IntPtr url, IntPtr result, IntPtr error)

        let selector name = sel_registerName name
        let class' name = objc_getClass name

        let url path =
            sendString (class' "NSURL", selector "fileURLWithPath:", path)

        let moveToTrash path =
            let manager = sendNoArgument (class' "NSFileManager", selector "defaultManager")

            trash (manager, selector "trashItemAtURL:resultingItemURL:error:", url path, IntPtr.Zero, IntPtr.Zero)
            <> 0uy

    type MacOS() =
        interface TrashBackend with
            member _.MoveToTrash(path) =
                try
                    if MacNative.moveToTrash path then
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
    static member CreateFreedesktop(dataHome: string) : TrashBackend =
        NativeTrash.Freedesktop(Path.GetFullPath dataHome)

    static member CreateForCurrentUser() : TrashBackend = NativeTrash.current ()

[<CLIMutable>]
type JournalStep =
    { Kind: string
      Source: string
      Destination: string
      Stage: string
      Backup: string
      Applied: bool }

[<CLIMutable>]
type Journal =
    { Version: int
      Id: string
      State: string
      Roots: string array
      Steps: JournalStep array
      CreatedUtc: DateTimeOffset }

module internal MutationJournal =
    let private options =
        JsonSerializerOptions(WriteIndented = false, PropertyNameCaseInsensitive = true)

    let private path root id = Path.Combine(root, $"{id}.json")

    let write root journal =
        MutationPaths.ensurePrivateDirectory root
        let target = path root journal.Id
        let pending = target + ".tmp"
        File.WriteAllText(pending, JsonSerializer.Serialize(journal, options), Encoding.UTF8)
        File.Move(pending, target, true)

    let private safeStep roots step =
        let authorised path =
            roots |> Array.exists (fun root -> MutationPaths.isUnder root path)

        let paths =
            [| step.Source; step.Destination |]
            |> Array.filter (String.IsNullOrEmpty >> not)

        let safeSidecar kind path =
            String.IsNullOrEmpty path
            || (not (MutationPaths.exists path) || not (MutationPaths.isLink path))
               && MutationPaths.ownedSidecar step.Destination kind path

        paths
        |> Array.forall (fun path -> MutationPaths.noFollow path |> Result.isOk && authorised path)
        && safeSidecar "stage" step.Stage
        && safeSidecar "backup" step.Backup

    let rollbackStep step =
        if not (String.IsNullOrEmpty step.Stage) && MutationPaths.exists step.Stage then
            MutationPaths.removeEntry step.Stage

        match step.Kind with
        | "replace" ->
            if File.Exists step.Backup then
                MutationPaths.removeEntry step.Destination
                MutationPaths.moveEntry step.Backup step.Destination
            elif
                (step.Applied || (not (File.Exists step.Stage) && File.Exists step.Destination))
                && File.Exists step.Destination
            then
                File.Delete step.Destination
        | "move" ->
            if not (MutationPaths.exists step.Source) then
                if MutationPaths.exists step.Stage then
                    MutationPaths.moveEntry step.Stage step.Source
                elif MutationPaths.exists step.Destination then
                    MutationPaths.copyTree step.Destination step.Source

            if MutationPaths.exists step.Backup then
                MutationPaths.removeEntry step.Destination
                MutationPaths.moveEntry step.Backup step.Destination
            elif
                (step.Applied || (not (File.Exists step.Stage) && File.Exists step.Destination))
                && MutationPaths.exists step.Destination
            then
                MutationPaths.removeEntry step.Destination
        | "trash"
        | "permanent-delete" when step.Applied -> invalidOp "An irreversible operation requires manual recovery."
        | "trash"
        | "permanent-delete" -> ()
        | _ -> invalidOp "The journal contains an unknown operation."

    let recover root (clock: MutationClock) =
        MutationPaths.ensurePrivateDirectory root
        let mutable manual = false

        for file in Directory.EnumerateFiles(root, "*.json") do
            try
                let journal =
                    JsonSerializer.Deserialize<Journal>(File.ReadAllText file, options)
                    |> Option.ofObj

                if journal.IsNone then
                    manual <- true
                else
                    let journal = journal.Value

                    if journal.Version <> 1 || journal.Roots.Length = 0 then
                        manual <- true
                    else
                        let roots = journal.Roots |> Array.map MutationPaths.noFollow

                        if roots |> Array.exists Result.isError then
                            manual <- true
                        else
                            let canonicalRoots = roots |> Array.choose Result.toOption

                            if journal.Steps |> Array.exists (safeStep canonicalRoots >> not) then
                                manual <- true
                            elif journal.State = "completed" then
                                if clock.UtcNow - journal.CreatedUtc > TimeSpan.FromDays 7.0 then
                                    journal.Steps
                                    |> Array.iter (fun step ->
                                        if File.Exists step.Backup then
                                            File.Delete step.Backup)

                                    File.Delete file
                            elif journal.State = "applied" then
                                write root { journal with State = "completed" }
                            elif
                                journal.State = "prepared"
                                || journal.State = "applying"
                                || journal.State = "rolling-back"
                            then
                                try
                                    journal.Steps |> Array.rev |> Array.iter rollbackStep
                                    write root { journal with State = "completed" }
                                with _ ->
                                    write
                                        root
                                        { journal with
                                            State = "manual-recovery" }

                                    manual <- true
                            else
                                manual <- true
            with _ ->
                manual <- true

        if manual then
            MutationRecoveryDisposition.PartialRecoveryRequired
        else
            MutationRecoveryDisposition.Ready

type private BoundPreview =
    { Digest: string
      ExpiresAtUtc: DateTimeOffset
      Fingerprints: Map<string, string> }

type MutationCoordinator
    (stateRoot: string, clock: MutationClock, revision: unit -> WorkspaceRevision, trash: TrashBackend) =
    let tokens = ConcurrentDictionary<string, BoundPreview>()

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
        Failure(UnsupportedCapability(WorkspaceCapabilityId.Write, diagnostic "unsupported_capability" message))

    let missing path =
        Failure(NotFound(path, diagnostic "not_found" $"The artifact does not exist: {path}"))

    let partial message =
        Failure(PartialRecoveryRequired("manual-recovery", diagnostic "partial_recovery_required" message))

    let conflict expected actual message =
        Failure(Conflict(expected, actual, diagnostic "workspace_conflict" message))

    let canonical (path: WorkspaceArtifactPath) = MutationPaths.noFollow path.Value

    let authorised (intents: ImmutableHashSet<MutationIntent>) roots path =
        match canonical path with
        | Error message -> Error message
        | Ok full ->
            let canonicalRoots = roots |> Seq.map canonical |> Seq.toArray

            if canonicalRoots |> Array.exists Result.isError || canonicalRoots.Length = 0 then
                Error "The path has no valid explicit authorization root."
            elif
                canonicalRoots
                |> Array.choose Result.toOption
                |> Array.exists (fun root -> MutationPaths.isUnder root full)
            then
                let workspaceRoot =
                    canonicalRoots[0] |> Result.toOption |> Option.defaultValue String.Empty

                if
                    MutationPaths.isUnder workspaceRoot full
                    || intents.Contains MutationIntent.AccessExternalPath
                then
                    Ok full
                else
                    Error "External path access requires explicit intent."
            else
                Error "The path is not explicitly authorized."

    let argumentText argument =
        match argument.Value with
        | Text value -> $"text:{value}"
        | Path value -> $"path:{value.Value}"
        | Boolean value -> $"bool:{value}"
        | Integer value -> $"int:{value}"
        | Node value -> $"node:{value.Value}"
        | Choice value -> $"choice:{value.Value}"

    let digest (request: MutationPreviewRequest) (targets: string array) (roots: string array) =
        let text =
            [ yield request.CommandId.Value
              yield string request.ExpectedRevision.Value
              yield! targets
              yield!
                  request.Arguments.Values
                  |> Seq.sortBy _.ParameterId.Value
                  |> Seq.map (fun argument -> $"{argument.ParameterId.Value}={argumentText argument}")
              yield! request.Intents |> Seq.map int |> Seq.sort |> Seq.map string
              yield! roots |> Seq.sort ]
            |> String.concat "\n"

        SHA256.HashData(Encoding.UTF8.GetBytes text) |> Convert.ToHexString

    let bind (request: MutationPreviewRequest) =
        let targets =
            request.Targets
            |> Seq.map (authorised request.Intents request.AuthorizedRoots)
            |> Seq.toArray

        let roots = request.AuthorizedRoots |> Seq.map canonical |> Seq.toArray

        match
            targets
            |> Array.tryPick (function
                | Error value -> Some value
                | Ok _ -> None),
            roots
            |> Array.tryPick (function
                | Error value -> Some value
                | Ok _ -> None)
        with
        | Some error, _
        | _, Some error -> Error error
        | None, None ->
            let targetPaths = targets |> Array.choose Result.toOption
            let rootPaths = roots |> Array.choose Result.toOption

            let fingerprints =
                targetPaths
                |> Array.map (fun path -> MutationPaths.fingerprint path |> Result.map (fun value -> path, value))

            match
                fingerprints
                |> Array.tryPick (function
                    | Error value -> Some value
                    | Ok _ -> None)
            with
            | Some error -> Error error
            | None ->
                Ok(
                    digest request targetPaths rootPaths,
                    targetPaths,
                    rootPaths,
                    fingerprints |> Array.choose Result.toOption |> Map.ofArray
                )

    let actionsValid
        (targets: string array)
        (intents: ImmutableHashSet<MutationIntent>)
        (actions: MutationAction array)
        =
        let bound path =
            MutationPaths.noFollow path
            |> Result.exists (fun full -> targets |> Array.exists ((=) full))

        let irreversible action =
            match action with
            | MutationAction.Trash _
            | MutationAction.Delete _ -> true
            | _ -> false

        let trailing =
            actions |> Array.skipWhile (irreversible >> not) |> Array.forall irreversible

        trailing
        && actions
           |> Array.forall (function
               | MutationAction.ReplaceFile(destination, _) ->
                   bound destination
                   && (not (File.Exists destination) || intents.Contains MutationIntent.Overwrite)
               | MutationAction.Move(source, destination) ->
                   bound source
                   && bound destination
                   && MutationPaths.exists source
                   && (not (MutationPaths.exists destination)
                       || intents.Contains MutationIntent.Overwrite)
                   && (not (MutationPaths.exists destination)
                       || File.Exists source = File.Exists destination)
               | MutationAction.Delete(path, permanent, recursive) ->
                   bound path
                   && MutationPaths.exists path
                   && (not permanent || intents.Contains MutationIntent.PermanentDelete)
                   && (not recursive || intents.Contains MutationIntent.RecursiveDelete)
               | MutationAction.Trash path -> bound path && MutationPaths.exists path)

    member _.Prepare(request: MutationPreviewRequest) =
        let actual = revision ()

        if actual.Value <> request.ExpectedRevision.Value then
            conflict request.ExpectedRevision actual "The workspace revision changed before preview."
        else
            match bind request with
            | Error message -> invalid message
            | Ok(binding, _, _, fingerprints) ->
                let token = Convert.ToHexString(RandomNumberGenerator.GetBytes 32)
                let expiry = clock.UtcNow.AddMinutes 5.0

                tokens[token] <-
                    { Digest = binding
                      ExpiresAtUtc = expiry
                      Fingerprints = fingerprints }

                Success
                    { Token = MutationConfirmationToken.Create token
                      ExpiresAtUtc = expiry
                      ExpectedRevision = request.ExpectedRevision }

    member _.Execute(token: MutationConfirmationToken, request: MutationPreviewRequest, actions: MutationAction seq) =
        match tokens.TryRemove token.Value with
        | false, _ -> invalid "The confirmation token is unknown or has already been used."
        | true, preview when clock.UtcNow > preview.ExpiresAtUtc -> invalid "The confirmation token has expired."
        | true, preview ->
            let actual = revision ()

            if actual.Value <> request.ExpectedRevision.Value then
                conflict request.ExpectedRevision actual "The workspace revision changed before execution."
            else
                match bind request with
                | Error message -> invalid message
                | Ok(binding, targets, roots, fingerprints) when binding <> preview.Digest ->
                    invalid "The confirmation token does not match this request."
                | Ok(_, _, _, fingerprints) when fingerprints <> preview.Fingerprints ->
                    conflict request.ExpectedRevision actual "An artifact changed after preview."
                | Ok(_, targets, roots, _) ->
                    let planned = actions |> Seq.toArray

                    if not (actionsValid targets request.Intents planned) then
                        invalid "The mutation lacks required intent, target binding, or path authorization."
                    elif
                        planned
                        |> Array.exists (function
                            | MutationAction.Trash _
                            | MutationAction.Delete(_, false, _) -> true
                            | _ -> false)
                        && isNull (box trash)
                    then
                        unsupported "Trash is unavailable."
                    else
                        let journal =
                            { Version = 1
                              Id = Guid.NewGuid().ToString "N"
                              State = "prepared"
                              Roots = roots
                              Steps = [||]
                              CreatedUtc = clock.UtcNow }

                        let mutable current = journal

                        let add step =
                            current <-
                                { current with
                                    State = "applying"
                                    Steps = Array.append current.Steps [| step |] }

                            MutationJournal.write stateRoot current
                            current.Steps.Length - 1

                        let applied index =
                            let steps = Array.copy current.Steps
                            steps[index] <- { steps[index] with Applied = true }
                            current <- { current with Steps = steps }
                            MutationJournal.write stateRoot current

                        try
                            MutationJournal.write stateRoot journal

                            for action in planned do
                                match action with
                                | MutationAction.ReplaceFile(destination, contents) ->
                                    let id = Guid.NewGuid().ToString "N"

                                    let step =
                                        { Kind = "replace"
                                          Source = ""
                                          Destination = destination
                                          Stage = MutationPaths.stageFor destination id
                                          Backup =
                                            (if MutationPaths.exists destination then
                                                 MutationPaths.backupFor destination id
                                             else
                                                 "")
                                          Applied = false }

                                    let index = add step
                                    File.WriteAllBytes(step.Stage, contents)

                                    if File.Exists destination then
                                        File.Replace(step.Stage, destination, step.Backup, true)
                                    else
                                        File.Move(step.Stage, destination)

                                    applied index
                                | MutationAction.Move(source, destination) ->
                                    let id = Guid.NewGuid().ToString "N"

                                    let step =
                                        { Kind = "move"
                                          Source = source
                                          Destination = destination
                                          Stage = MutationPaths.stageFor destination id
                                          Backup =
                                            (if File.Exists destination then
                                                 MutationPaths.backupFor destination id
                                             else
                                                 "")
                                          Applied = false }

                                    let index = add step

                                    let sameVolume =
                                        try
                                            MutationPaths.moveEntry source step.Stage
                                            true
                                        with :? IOException ->
                                            false

                                    if not sameVolume then
                                        MutationPaths.copyTree source step.Stage

                                        if MutationPaths.fingerprint source <> MutationPaths.fingerprint step.Stage then
                                            invalidOp "Staged copy verification failed."

                                    if MutationPaths.exists destination then
                                        if File.Exists destination then
                                            File.Replace(step.Stage, destination, step.Backup, true)
                                        else
                                            Directory.Move(destination, step.Backup)
                                            Directory.Move(step.Stage, destination)
                                    else
                                        MutationPaths.moveEntry step.Stage destination

                                    if not sameVolume then
                                        MutationPaths.removeEntry source

                                    applied index
                                | MutationAction.Delete(path, permanent, recursive) ->
                                    if permanent then
                                        let index =
                                            add
                                                { Kind = "permanent-delete"
                                                  Source = path
                                                  Destination = ""
                                                  Stage = ""
                                                  Backup = ""
                                                  Applied = false }

                                        if File.Exists path then
                                            File.Delete path
                                        else
                                            Directory.Delete(path, recursive)

                                        applied index
                                    else
                                        let index =
                                            add
                                                { Kind = "trash"
                                                  Source = path
                                                  Destination = ""
                                                  Stage = ""
                                                  Backup = ""
                                                  Applied = false }

                                        match trash.MoveToTrash path with
                                        | Ok() -> applied index
                                        | Error failure -> invalidOp failure.Message
                                | MutationAction.Trash path ->
                                    let index =
                                        add
                                            { Kind = "trash"
                                              Source = path
                                              Destination = ""
                                              Stage = ""
                                              Backup = ""
                                              Applied = false }

                                    match trash.MoveToTrash path with
                                    | Ok() -> applied index
                                    | Error failure -> invalidOp failure.Message

                            MutationJournal.write stateRoot { current with State = "applied" }
                            MutationJournal.write stateRoot { current with State = "completed" }
                            Success()
                        with ex ->
                            try
                                MutationJournal.write stateRoot { current with State = "rolling-back" }
                                current.Steps |> Array.rev |> Array.iter MutationJournal.rollbackStep
                                MutationJournal.write stateRoot { current with State = "completed" }
                                invalid ex.Message
                            with _ ->
                                MutationJournal.write
                                    stateRoot
                                    { current with
                                        State = "manual-recovery" }

                                partial "Rollback could not restore a safe filesystem state."

    static member CreateProduction(revision: unit -> WorkspaceRevision) =
        MutationCoordinator(MutationPaths.stateRoot (), SystemMutationClock(), revision, NativeTrash.current ())

    static member RecoverStartup() =
        MutationJournal.recover (MutationPaths.stateRoot ()) (SystemMutationClock())

    static member Recover(stateRoot: string, clock: MutationClock) =
        MutationJournal.recover (Path.GetFullPath stateRoot) clock
