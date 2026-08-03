namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.WorkspaceEditing

open System
open System.Collections.Immutable
open System.IO
open System.Threading

type private OwnedFileImage =
    | Missing
    | FileContents of byte array
    | NonFile

type internal OwnedFileSnapshot =
    private
        { Path: string
          Before: OwnedFileImage
          Expected: OwnedFileImage option }

type internal CreatedOutput = { Path: string; Fingerprint: string }

type internal OutputDirectorySnapshot =
    { Root: string
      Existed: bool
      ProjectFiles: Map<string, string> }

module internal DotnetCommandCompensation =
    let private projectExtensions = set [ ".csproj"; ".fsproj"; ".vbproj" ]

    let private fileImage path =
        if File.Exists path && not (ArtifactFiles.isLink path) then
            FileContents(File.ReadAllBytes path)
        elif ArtifactFiles.exists path then
            NonFile
        else
            Missing

    let private sameImage left right =
        match left, right with
        | Missing, Missing
        | NonFile, NonFile -> true
        | FileContents leftContents, FileContents rightContents -> leftContents = rightContents
        | _ -> false

    let outputSnapshot root =
        let root = Path.GetFullPath root

        let projects =
            if Directory.Exists root && not (ArtifactFiles.isLink root) then
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                |> Seq.choose (fun path ->
                    let extension =
                        Path.GetExtension path
                        |> Option.ofObj
                        |> Option.defaultValue String.Empty
                        |> _.ToLowerInvariant()

                    if projectExtensions.Contains extension then
                        Some(ArtifactFiles.identity path, path)
                    else
                        None)
                |> Map.ofSeq
            else
                Map.empty

        { Root = root
          Existed = ArtifactFiles.exists root
          ProjectFiles = projects }

    let newProjectFiles beforeSnapshot afterSnapshot =
        afterSnapshot.ProjectFiles
        |> Seq.choose (fun (KeyValue(identity, path)) ->
            if beforeSnapshot.ProjectFiles.ContainsKey identity then
                None
            else
                Some path)
        |> Seq.toArray

    let newOutputRoot beforeSnapshot afterSnapshot =
        if beforeSnapshot.Existed || not afterSnapshot.Existed then
            None
        else
            match ArtifactFiles.fingerprint afterSnapshot.Root with
            | Ok fingerprint ->
                Some
                    { Path = afterSnapshot.Root
                      Fingerprint = fingerprint }
            | Error error -> invalidOp error

    let outputArtifacts root =
        if Directory.Exists root && not (ArtifactFiles.isLink root) then
            let entries =
                Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                |> Seq.toArray

            match entries |> Array.tryFind ArtifactFiles.isLink with
            | Some path -> Error $"Template output contains a symbolic link: {path}"
            | None ->
                entries
                |> Array.map Path.GetFullPath
                |> Array.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
                |> Ok
        else
            Error "The template output directory is unavailable."

    let expectedOutputArtifacts root outputs =
        let root = Path.GetFullPath root

        let rec parents (path: string) =
            match Path.GetDirectoryName path |> Option.ofObj with
            | Some parent when
                not (String.Equals(parent, root, StringComparison.Ordinal))
                && ArtifactFiles.isUnder root parent
                ->
                parent :: parents parent
            | _ -> []

        outputs
        |> Seq.collect (fun path ->
            let path = Path.GetFullPath path
            path :: parents path)
        |> Seq.distinct
        |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
        |> Seq.toArray

    let snapshotFiles paths =
        paths
        |> Seq.map (fun (path: WorkspaceArtifactPath) ->
            { Path = path.Value
              Before = fileImage path.Value
              Expected = None })
        |> Seq.toArray

    let captureExpectedFiles snapshots =
        snapshots
        |> Array.map (fun snapshot ->
            { snapshot with
                Expected = Some(fileImage snapshot.Path) })

    let snapshotPaths (snapshots: OwnedFileSnapshot array) = snapshots |> Seq.map _.Path

    let restoreFiles
        (coordinator: WorkspaceEditTransaction)
        workspaceRoot
        currentRevision
        commandId
        arguments
        (snapshots: OwnedFileSnapshot array)
        =
        let conflicts = ResizeArray<string>()

        let actions =
            snapshots
            |> Seq.choose (fun snapshot ->
                let current = fileImage snapshot.Path

                match snapshot.Expected with
                | None when sameImage current snapshot.Before -> None
                | None ->
                    conflicts.Add $"{snapshot.Path} (command-owned state was not captured)"
                    None
                | Some _ when sameImage current snapshot.Before -> None
                | Some expected when not (sameImage current expected) ->
                    conflicts.Add $"{snapshot.Path} (changed after the command)"
                    None
                | Some _ ->
                    match snapshot.Before with
                    | FileContents contents ->
                        Some(WorkspaceEditAction.ReplaceFile(snapshot.Path, contents))
                    | Missing -> Some(WorkspaceEditAction.PermanentDelete(snapshot.Path, false))
                    | NonFile ->
                        conflicts.Add $"{snapshot.Path} (original path was not a regular file)"
                        None)
            |> Seq.toArray

        let verified () =
            snapshots
            |> Array.forall (fun snapshot -> sameImage (fileImage snapshot.Path) snapshot.Before)

        if conflicts.Count > 0 then
            Error(String.concat ", " conflicts)
        elif actions.Length = 0 then
            if verified () then
                Ok()
            else
                Error "The original files could not be verified."
        else
            let request =
                { CommandId = commandId
                  Targets =
                    snapshots
                    |> Seq.map (fun snapshot -> WorkspaceArtifactPath.Create snapshot.Path)
                    |> ImmutableArray.CreateRange
                  Arguments = arguments
                  ExpectedRevision = WorkspaceRevision.Create(currentRevision ())
                  Intents =
                    ImmutableHashSet.Create(
                        WorkspaceEditIntent.Overwrite,
                        WorkspaceEditIntent.PermanentDelete
                    )
                  AuthorizedRoots =
                    ImmutableArray.Create(WorkspaceArtifactPath.Create workspaceRoot) }

            match coordinator.Prepare(request, actions) with
            | Failure failure -> Error failure.Diagnostic.Message
            | Success preview ->
                match
                    coordinator.Execute(
                        request,
                        actions,
                        preview.Confirmation,
                        CancellationToken.None
                    )
                with
                | Success Applied when verified () -> Ok()
                | Success Applied ->
                    Error "The original files were restored but could not be verified."
                | Success(RolledBack failure)
                | Failure failure -> Error failure.Diagnostic.Message

    let removeNewOutput entry =
        try
            match ArtifactFiles.fingerprint entry.Path with
            | Ok fingerprint when fingerprint = entry.Fingerprint ->
                ArtifactFiles.remove entry.Path

                if ArtifactFiles.exists entry.Path then
                    Some entry.Path
                else
                    None
            | Ok _ -> Some $"{entry.Path} (changed after creation)"
            | Error error -> Some $"{entry.Path} ({error})"
        with error ->
            Some $"{entry.Path} ({error.Message})"
