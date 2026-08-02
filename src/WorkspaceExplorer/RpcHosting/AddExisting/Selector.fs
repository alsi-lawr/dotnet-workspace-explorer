namespace Dotnet.WorkspaceExplorer

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.Workspaces

[<RequireQualifiedAccess>]
module internal AddExistingSelectorPaths =
    let rootDisplayName canonicalRoot =
        let directory = DirectoryInfo canonicalRoot
        let name = directory.Name

        if isNull directory.Parent || String.IsNullOrWhiteSpace name then
            "Filesystem Root"
        else
            name

type internal AddExistingSelector
    (
        maximumPageSize: unit -> int,
        clock: TimeProvider,
        readGitPathSnapshotAsync:
            CancellationToken
                -> System.Threading.Tasks.Task<Result<WorkspaceGitPathSnapshot option, RpcError>>
    ) =
    let gate = obj ()
    let mutable current: AddExistingSession option = None

    let failure code message =
        Error(RpcErrors.create code message None)

    let invalid message = failure "invalid_params" message
    let unavailable message = failure "selector_unavailable" message

    let randomId () =
        RandomNumberGenerator.GetBytes 24 |> Convert.ToHexString |> _.ToLowerInvariant()

    let pathIdentity (path: string) = ArtifactFiles.identity path

    let gitPathIdentity path =
        pathIdentity path |> WorkspaceGitPaths.withoutTrailingDirectorySeparators

    let extension (path: string) =
        Path.GetExtension path |> Option.ofObj |> Option.defaultValue String.Empty

    let projectExtension (path: string) =
        match extension path |> _.ToLowerInvariant() with
        | ".csproj"
        | ".fsproj"
        | ".vbproj" -> true
        | _ -> false

    let unsupportedSolutionFile (path: string) =
        match extension path |> _.ToLowerInvariant() with
        | ".sln"
        | ".slnx"
        | ".slnf" -> true
        | _ -> false

    let iconHint (path: string) isDirectory =
        if isDirectory then
            Some "folder"
        elif projectExtension path then
            Some((extension path).TrimStart('.').ToLowerInvariant())
        else
            Some((extension path).TrimStart('.'))
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.map _.ToLowerInvariant()

    let snapshotFingerprint presentationVersion2 (entries: AddExistingEntry array) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)

        for entry in entries do
            writer.Write entry.DisplayName
            writer.Write entry.IsDirectory
            writer.Write entry.IsLink
            writer.Write entry.Selectable
            writer.Write entry.Expandable
            writer.Write(entry.IconHint |> Option.defaultValue String.Empty)
            writer.Write(entry.Fingerprint |> Option.defaultValue String.Empty)

            if presentationVersion2 then
                writer.Write(
                    match entry.Availability with
                    | AddExistingAvailability.Available -> "available"
                    | AddExistingAvailability.AlreadyPresent -> "alreadyPresent"
                    | AddExistingAvailability.Ineligible -> "ineligible"
                )

                writer.Write entry.GitStates.Length

                for state in entry.GitStates do
                    writer.Write(
                        match state with
                        | GitStatusState.Staged -> "staged"
                        | GitStatusState.Unstaged -> "unstaged"
                        | GitStatusState.Renamed -> "renamed"
                        | GitStatusState.Deleted -> "deleted"
                        | GitStatusState.Unmerged -> "unmerged"
                        | GitStatusState.Untracked -> "untracked"
                        | GitStatusState.Ignored -> "ignored"
                    )

        writer.Flush()
        SHA256.HashData(stream.ToArray()) |> Convert.ToHexString

    let sessionValid expectedRevision selectorId =
        match current with
        | Some session when
            session.Id = selectorId
            && session.Revision = expectedRevision
            && clock.GetUtcNow() <= session.ExpiresAtUtc
            ->
            Ok session
        | Some session when session.Revision <> expectedRevision ->
            current <- None
            unavailable "The workspace revision changed after the selector started."
        | Some session when clock.GetUtcNow() > session.ExpiresAtUtc ->
            current <- None
            unavailable "The Add Existing selector expired."
        | _ -> unavailable "The Add Existing selector is unknown or no longer active."

    let pageSize requested =
        requested
        |> Option.defaultValue (min 256 (maximumPageSize ()))
        |> min (maximumPageSize ())
        |> max 1

    let selectionAvailability (session: AddExistingSession) path isDirectory isLink =
        if isLink then
            false, AddExistingAvailability.Ineligible
        elif isDirectory then
            session.DirectorySelectionVersion1,
            if session.DirectorySelectionVersion1 then
                AddExistingAvailability.Available
            else
                AddExistingAvailability.Ineligible
        elif session.RegisteredPaths.Contains(pathIdentity path) then
            false, AddExistingAvailability.AlreadyPresent
        else
            let selectable =
                match session.Target.Node.Kind with
                | WorkspaceNodeKind.Workspace -> projectExtension path
                | WorkspaceNodeKind.SolutionFolder ->
                    projectExtension path || not (unsupportedSolutionFile path)
                | WorkspaceNodeKind.Project
                | WorkspaceNodeKind.ProjectFolder -> not (projectExtension path)
                | _ -> false

            selectable,
            if selectable then
                AddExistingAvailability.Available
            else
                AddExistingAvailability.Ineligible

    let gitStates (session: AddExistingSession) path isDirectory =
        match session.GitSnapshot with
        | None -> [||]
        | Some snapshot ->
            snapshot.Entries
            |> Seq.collect (fun entry ->
                let exact = gitPathIdentity path = gitPathIdentity entry.Path

                if exact then
                    entry.States
                elif isDirectory && ArtifactFiles.isUnder path entry.Path then
                    entry.States |> Array.filter ((<>) GitStatusState.Ignored)
                else
                    [||])
            |> GitStatusStates.normalize

    let createEntry (session: AddExistingSession) canonical path =
        let full = Path.GetFullPath path
        let link = ArtifactFiles.isLink full
        let directory = Directory.Exists full
        let selectable, availability = selectionAvailability session full directory link

        let fingerprint =
            if directory || link then
                None
            else
                ArtifactFiles.fingerprint full |> Result.toOption

        { Id = randomId ()
          Path = full
          ParentPath = canonical
          DisplayName = Path.GetFileName full |> Option.ofObj |> Option.defaultValue full
          IsDirectory = directory
          IsLink = link
          Selectable = selectable
          Availability = availability
          GitStates = gitStates session full directory
          Expandable = directory && not link
          IconHint = iconHint full directory
          Fingerprint = fingerprint }

    let enumerate (session: AddExistingSession) directory =
        match ArtifactFiles.canonicalNoFollow false directory with
        | Error _ -> invalid "The requested directory is outside the selector boundary."
        | Ok canonical when not (ArtifactFiles.isUnder session.RootPath canonical) ->
            invalid "The requested directory is outside the selector boundary."
        | Ok canonical when not (Directory.Exists canonical) ->
            invalid "The requested selector directory no longer exists."
        | Ok canonical ->
            try
                let entries =
                    Directory.EnumerateFileSystemEntries canonical
                    |> Seq.map (createEntry session canonical)
                    |> Seq.sortWith (fun left right ->
                        let kind =
                            compare
                                (if left.IsDirectory then 0 else 1)
                                (if right.IsDirectory then 0 else 1)

                        if kind <> 0 then
                            kind
                        else
                            let name =
                                StringComparer.Ordinal.Compare(left.DisplayName, right.DisplayName)

                            if name <> 0 then
                                name
                            else
                                StringComparer.Ordinal.Compare(left.Path, right.Path))
                    |> Seq.toArray

                let fingerprint = snapshotFingerprint session.PresentationVersion2 entries

                match session.Snapshots.TryGetValue canonical with
                | true, previous when previous.Fingerprint <> fingerprint ->
                    invalid "The selector directory changed while it was being browsed."
                | true, previous -> Ok previous
                | _ ->
                    for entry in entries do
                        session.Entries[entry.Id] <- entry

                    let snapshot =
                        { Fingerprint = fingerprint
                          Entries = entries }

                    session.Snapshots[canonical] <- snapshot
                    Ok snapshot
            with
            | :? UnauthorizedAccessException ->
                invalid "The requested selector directory cannot be read."
            | :? IOException -> invalid "The requested selector directory cannot be read."

    let pageFrom session parentEntryId directory requested offset expectedSnapshot =
        match enumerate session directory with
        | Error error -> Error error
        | Ok snapshot when
            expectedSnapshot
            |> Option.exists (fun expected -> expected <> snapshot.Fingerprint)
            ->
            invalid "The selector continuation is stale."
        | Ok snapshot ->
            let count = pageSize requested
            let entries = snapshot.Entries |> Array.skip offset |> Array.truncate count
            let nextOffset = offset + entries.Length

            let nextToken =
                if nextOffset < snapshot.Entries.Length then
                    let token = randomId ()

                    session.Continuations[token] <-
                        { ParentEntryId = parentEntryId
                          ParentPath = directory
                          Snapshot = snapshot.Fingerprint
                          Offset = nextOffset }

                    Some token
                else
                    None

            Ok(entries, nextToken)

    let registeredPathsAsync
        (workspace: SolutionWorkspace)
        (state: WorkspaceIndex)
        (target: WorkspaceSemanticContext)
        (cancellationToken: CancellationToken)
        =
        task {
            let paths = HashSet<string>(StringComparer.Ordinal)

            for project in workspace.Contents.Projects do
                paths.Add(pathIdentity project.Path.AbsolutePath.Value) |> ignore

            let solutionDirectory =
                Path.GetDirectoryName workspace.SolutionPath.Value
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            for item in workspace.Contents.Items do
                paths.Add(pathIdentity (Path.GetFullPath(item.RelativePath, solutionDirectory)))
                |> ignore

            match target.ProjectId with
            | None -> return Ok paths
            | Some projectId ->
                match! state.ProjectAsync(projectId, cancellationToken) with
                | Failure failure -> return Error(WorkspaceRpcResponses.failureError failure)
                | Success(_, _, snapshot) ->
                    for dimension in snapshot.Dimensions do
                        for item in dimension.Items do
                            item.ResolvedPath
                            |> Option.ofObj
                            |> Option.iter (fun path ->
                                paths.Add(pathIdentity path.Value) |> ignore)

                    return Ok paths
        }

    member _.StartAsync
        (
            workspace: SolutionWorkspace,
            state: WorkspaceIndex,
            target: WorkspaceSemanticContext,
            selectionId: string,
            expectedRevision: int64,
            requestedPageSize: int option,
            presentationVersion2: bool,
            directorySelectionVersion1: bool,
            cancellationToken: CancellationToken
        ) =
        task {
            if state.Revision <> expectedRevision then
                return Error(WorkspaceRpcResponses.workspaceConflict state.Revision)
            elif workspace.Descriptor.IsReadOnly then
                return Error(RpcErrors.unsupported "The selected .slnf workspace is read-only.")
            else
                let root =
                    (match target.Node.Kind with
                     | WorkspaceNodeKind.Workspace
                     | WorkspaceNodeKind.SolutionFolder ->
                         Path.GetDirectoryName workspace.SolutionPath.Value |> Option.ofObj
                     | WorkspaceNodeKind.Project
                     | WorkspaceNodeKind.ProjectFolder ->
                         target.ProjectPath
                         |> Option.bind (fun path ->
                             Path.GetDirectoryName path.Value |> Option.ofObj)
                     | _ -> None)

                match root with
                | None -> return Error(RpcErrors.unsupported "Add Existing is unavailable here.")
                | Some root ->
                    match ArtifactFiles.canonicalNoFollow false root with
                    | Error message -> return invalid message
                    | Ok canonicalRoot ->
                        let! registered =
                            registeredPathsAsync workspace state target cancellationToken

                        match registered with
                        | Error error -> return Error error
                        | Ok registered ->
                            let! acquiredGitSnapshot =
                                if presentationVersion2 then
                                    readGitPathSnapshotAsync cancellationToken
                                else
                                    System.Threading.Tasks.Task.FromResult(Ok None)

                            let gitSnapshot =
                                acquiredGitSnapshot |> Result.toOption |> Option.flatten

                            let selectorRevision = state.Revision
                            let selectorId = randomId ()

                            let provisionalRootEntry =
                                { Id = randomId ()
                                  Path = canonicalRoot
                                  ParentPath = canonicalRoot
                                  DisplayName =
                                    AddExistingSelectorPaths.rootDisplayName canonicalRoot
                                  IsDirectory = true
                                  IsLink = false
                                  Selectable = false
                                  Availability = AddExistingAvailability.Ineligible
                                  GitStates = [||]
                                  Expandable = true
                                  IconHint = Some "folder"
                                  Fingerprint = None }

                            let provisionalSession =
                                { Id = selectorId
                                  SelectionId = selectionId
                                  Revision = selectorRevision
                                  ExpiresAtUtc = clock.GetUtcNow().AddMinutes 10.0
                                  RootPath = canonicalRoot
                                  RootEntry = provisionalRootEntry
                                  Target = target
                                  Entries = Dictionary(StringComparer.Ordinal)
                                  Snapshots = Dictionary(StringComparer.Ordinal)
                                  Continuations = Dictionary(StringComparer.Ordinal)
                                  RegisteredPaths = registered
                                  PresentationVersion2 = presentationVersion2
                                  DirectorySelectionVersion1 = directorySelectionVersion1
                                  GitSnapshot = gitSnapshot }

                            let rootEntry =
                                { provisionalRootEntry with
                                    GitStates = gitStates provisionalSession canonicalRoot true }

                            let session =
                                { provisionalSession with
                                    RootEntry = rootEntry }

                            session.Entries[rootEntry.Id] <- rootEntry

                            let result =
                                lock gate (fun () ->
                                    current <- Some session

                                    pageFrom
                                        session
                                        rootEntry.Id
                                        rootEntry.Path
                                        requestedPageSize
                                        0
                                        None)

                            return
                                result
                                |> Result.map (fun (entries, nextToken) ->
                                    AddExistingFormatting.start
                                        selectorRevision
                                        selectorId
                                        session.ExpiresAtUtc
                                        presentationVersion2
                                        rootEntry
                                        entries
                                        nextToken)
        }

    member this.StartAsync
        (
            workspace: SolutionWorkspace,
            state: WorkspaceIndex,
            target: WorkspaceSemanticContext,
            selectionId: string,
            expectedRevision: int64,
            requestedPageSize: int option,
            presentationVersion2: bool,
            cancellationToken: CancellationToken
        ) =
        this.StartAsync(
            workspace,
            state,
            target,
            selectionId,
            expectedRevision,
            requestedPageSize,
            presentationVersion2,
            false,
            cancellationToken
        )

    member this.StartAsync
        (
            workspace: SolutionWorkspace,
            state: WorkspaceIndex,
            target: WorkspaceSemanticContext,
            selectionId: string,
            expectedRevision: int64,
            requestedPageSize: int option,
            cancellationToken: CancellationToken
        ) =
        this.StartAsync(
            workspace,
            state,
            target,
            selectionId,
            expectedRevision,
            requestedPageSize,
            false,
            false,
            cancellationToken
        )

    member _.Children
        (
            selectorId: string,
            parentEntryId: string,
            requestedPageSize: int option,
            continuationToken: string option,
            revision: int64
        ) =
        lock gate (fun () ->
            match sessionValid revision selectorId with
            | Error error -> Error error
            | Ok session ->
                match continuationToken with
                | Some token ->
                    match session.Continuations.TryGetValue token with
                    | false, _ -> invalid "The selector continuation token is unknown or stale."
                    | true, continuation when continuation.ParentEntryId <> parentEntryId ->
                        invalid "The selector continuation does not match its parent."
                    | true, continuation ->
                        session.Continuations.Remove token |> ignore

                        pageFrom
                            session
                            parentEntryId
                            continuation.ParentPath
                            requestedPageSize
                            continuation.Offset
                            (Some continuation.Snapshot)
                        |> Result.map (fun (entries, nextToken) ->
                            AddExistingFormatting.page
                                session.PresentationVersion2
                                revision
                                selectorId
                                parentEntryId
                                entries
                                nextToken)
                | None ->
                    match session.Entries.TryGetValue parentEntryId with
                    | true, parent when parent.Expandable ->
                        pageFrom session parentEntryId parent.Path requestedPageSize 0 None
                        |> Result.map (fun (entries, nextToken) ->
                            AddExistingFormatting.page
                                session.PresentationVersion2
                                revision
                                selectorId
                                parentEntryId
                                entries
                                nextToken)
                    | _ -> invalid "The selector parent is unknown or not expandable.")

    member _.Close(selectorId: string) =
        lock gate (fun () ->
            match current with
            | Some session when session.Id = selectorId ->
                current <- None
                Ok(RpcValue.map [ "closed", RpcValue.Boolean true ])
            | _ -> unavailable "The Add Existing selector is unknown or no longer active.")

    member _.Invalidate() = lock gate (fun () -> current <- None)

    member _.ResolveSelection(selectorId, expectedRevision, targetNodeId, entryIds: string array) =
        lock gate (fun () ->
            match sessionValid expectedRevision selectorId with
            | Error error -> Error error
            | Ok session when session.Target.Node.Id.Value <> targetNodeId ->
                invalid "The selector target does not match the command target."
            | Ok _ when entryIds.Length = 0 || entryIds.Length > 256 ->
                invalid "Add Existing requires between 1 and 256 entries."
            | Ok session ->
                let unique = HashSet<string>(StringComparer.Ordinal)
                let requested = ResizeArray<AddExistingEntry>()
                let mutable error = None

                for id in entryIds do
                    if error.IsNone then
                        if String.IsNullOrWhiteSpace id || not (unique.Add id) then
                            error <- Some "Entry IDs must be unique non-empty values."
                        else
                            match session.Entries.TryGetValue id with
                            | true, entry when
                                entry.Selectable
                                && (not entry.IsDirectory || session.DirectorySelectionVersion1)
                                ->
                                requested.Add entry
                            | _ ->
                                error <-
                                    Some
                                        "The selected entry is unavailable in this Add Existing context."

                let requestedIdentities = HashSet<string>(StringComparer.Ordinal)

                for entry in requested do
                    if not (requestedIdentities.Add(pathIdentity entry.Path)) then
                        error <- Some "Selected entries collide under filesystem identity rules."

                let selected = ResizeArray<AddExistingEntry>()

                let strictDescendant (ancestor: AddExistingEntry) (candidate: AddExistingEntry) =
                    pathIdentity ancestor.Path <> pathIdentity candidate.Path
                    && ArtifactFiles.isUnder ancestor.Path candidate.Path

                for entry in requested do
                    if error.IsNone then
                        for index = selected.Count - 1 downto 0 do
                            let previous = selected[index]

                            if
                                (entry.IsDirectory && strictDescendant entry previous)
                                || (previous.IsDirectory && strictDescendant previous entry)
                            then
                                selected.RemoveAt index

                        selected.Add entry

                let resolved = ResizeArray<AddExistingResolvedEntry>()
                let resolvedIdentities = HashSet<string>(StringComparer.Ordinal)
                let traversedDirectories = Dictionary<string, string>(StringComparer.Ordinal)

                let directorySegments (source: AddExistingEntry) (entry: AddExistingEntry) =
                    let directory =
                        Path.GetDirectoryName entry.Path
                        |> Option.ofObj
                        |> Option.defaultValue source.Path

                    let relative = Path.GetRelativePath(source.ParentPath, directory)

                    if relative = "." then
                        [||]
                    else
                        relative.Split(
                            [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |],
                            StringSplitOptions.RemoveEmptyEntries
                        )

                let addResolved (source: AddExistingEntry) recursive (entry: AddExistingEntry) =
                    if not (resolvedIdentities.Add(pathIdentity entry.Path)) then
                        error <- Some "Recursive selections resolve to the same workspace item."
                    elif resolved.Count = 256 then
                        error <-
                            Some
                                "The selected directories contain more than 256 eligible items; choose a smaller directory."
                    else
                        resolved.Add
                            { Entry = entry
                              DirectorySegments =
                                if recursive then directorySegments source entry else [||]
                              Recursive = recursive }

                let expandDirectory (source: AddExistingEntry) =
                    let discovered = ResizeArray<AddExistingEntry>()

                    let rec walk (directory: string) =
                        if error.IsNone then
                            match enumerate session directory with
                            | Error failure -> error <- Some failure.Message
                            | Ok snapshot ->
                                traversedDirectories[pathIdentity directory] <- directory

                                for entry in snapshot.Entries do
                                    if error.IsNone then
                                        if entry.IsLink then
                                            error <-
                                                Some
                                                    $"The selected directory contains a symbolic link: {entry.DisplayName}."
                                        elif entry.IsDirectory then
                                            walk entry.Path
                                        elif
                                            entry.Selectable
                                            && entry.Availability = AddExistingAvailability.Available
                                        then
                                            discovered.Add entry

                                            if resolved.Count + discovered.Count > 256 then
                                                error <-
                                                    Some
                                                        "The selected directories contain more than 256 eligible items; choose a smaller directory."

                    walk source.Path

                    if error.IsNone && discovered.Count = 0 then
                        error <-
                            Some
                                $"The selected directory '{source.DisplayName}' contains no items eligible for this target."

                    if error.IsNone then
                        discovered
                        |> Seq.sortWith (fun (left: AddExistingEntry) (right: AddExistingEntry) ->
                            StringComparer.Ordinal.Compare(
                                Path.GetRelativePath(source.Path, left.Path),
                                Path.GetRelativePath(source.Path, right.Path)
                            ))
                        |> Seq.iter (addResolved source true)

                for source in selected do
                    if error.IsNone then
                        match
                            session.Snapshots.TryGetValue source.ParentPath,
                            enumerate session source.ParentPath
                        with
                        | (true, expected), Ok actual when
                            expected.Fingerprint = actual.Fingerprint
                            ->
                            if source.IsDirectory then
                                expandDirectory source
                            else
                                match
                                    source.Fingerprint,
                                    ArtifactFiles.fingerprint source.Path |> Result.toOption
                                with
                                | Some expectedFingerprint, Some actualFingerprint when
                                    expectedFingerprint = actualFingerprint
                                    ->
                                    addResolved source false source
                                | _ -> error <- Some "A selected source file changed."
                        | _ -> error <- Some "A selected source or directory changed."

                for directory in traversedDirectories.Values do
                    if error.IsNone then
                        match session.Snapshots.TryGetValue directory with
                        | true, expected ->
                            match enumerate session directory with
                            | Ok actual when actual.Fingerprint = expected.Fingerprint -> ()
                            | _ -> error <- Some "A selected directory changed during traversal."
                        | _ -> error <- Some "A selected directory snapshot is unavailable."

                for item in resolved do
                    if error.IsNone then
                        match
                            item.Entry.Fingerprint,
                            ArtifactFiles.fingerprint item.Entry.Path |> Result.toOption
                        with
                        | Some expectedFingerprint, Some actualFingerprint when
                            expectedFingerprint = actualFingerprint
                            ->
                            ()
                        | _ -> error <- Some "A recursively selected source file changed."

                match error with
                | Some message -> invalid message
                | None ->
                    Ok(
                        session,
                        { Sources = selected.ToArray()
                          Entries = resolved.ToArray() }
                    ))

    interface IDisposable with
        member this.Dispose() = this.Invalidate()

    new(maximumPageSize, clock) =
        new AddExistingSelector(
            maximumPageSize,
            clock,
            fun _ -> System.Threading.Tasks.Task.FromResult(Ok None)
        )
