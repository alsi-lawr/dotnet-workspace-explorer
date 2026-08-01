namespace Dotnet.WorkspaceExplorer

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open Dotnet.WorkspaceExplorer.CommandLine
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.Workspaces

[<RequireQualifiedAccess>]
type internal WorkspaceCreateKind =
    | Empty
    | ItemTemplate
    | ProjectTemplate
    | SolutionFolder
    | AddExisting

[<RequireQualifiedAccess>]
type internal WorkspaceCreateExecution =
    | Transaction
    | Operation

type internal WorkspaceTemplateEntry =
    { SelectionId: string
      Kind: WorkspaceCreateKind
      DisplayName: string
      Description: string
      Language: string option
      Execution: WorkspaceCreateExecution
      Identity: string
      ShortName: string
      Fingerprint: string }

type internal WorkspaceTemplateCatalog =
    { Fingerprint: string
      EmptySelectionId: string
      Entries: WorkspaceTemplateEntry array }

type internal WorkspaceTemplateBinding =
    { SelectionId: string
      Fingerprint: string
      Identity: string
      ShortName: string
      Kind: WorkspaceCreateKind
      Language: string option }

[<RequireQualifiedAccess>]
module internal WorkspaceTemplateCatalog =
    type private TemplateCacheRead =
        | Found of byte array
        | Missing
        | Unavailable

    let private cacheInitialization = new SemaphoreSlim(1, 1)

    let private invalid code message = RpcErrors.create code message None

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &value) then
            Some value
        else
            None

    let private textProperty (name: string) (element: JsonElement) : string option =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.String ->
            let text = string (value.GetString())
            if String.IsNullOrWhiteSpace text then None else Some text
        | _ -> None

    let private integerProperty (name: string) (element: JsonElement) =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt32() with
            | true, parsed -> parsed
            | _ -> 0
        | _ -> 0

    let private tag name (element: JsonElement) =
        match tryProperty "TagsCollection" element with
        | Some tags when tags.ValueKind = JsonValueKind.Object -> textProperty name tags
        | _ -> None

    let private shortName (element: JsonElement) : string option =
        match tryProperty "ShortNameList" element with
        | Some names when names.ValueKind = JsonValueKind.Array ->
            names.EnumerateArray()
            |> Seq.tryPick (fun value ->
                if value.ValueKind = JsonValueKind.String then
                    let text = string (value.GetString())
                    if String.IsNullOrWhiteSpace text then None else Some text
                else
                    None)
        | _ -> None

    let private kind (element: JsonElement) =
        match tag "type" element |> Option.map _.ToLowerInvariant() with
        | Some "item" -> Some WorkspaceCreateKind.ItemTemplate
        | Some "project" -> Some WorkspaceCreateKind.ProjectTemplate
        | _ -> None

    let private language (element: JsonElement) =
        tag "language" element
        |> Option.map (fun value ->
            match value.ToUpperInvariant() with
            | "C#" -> "C#"
            | "F#" -> "F#"
            | "VB"
            | "VISUAL BASIC" -> "VB"
            | _ -> value)

    let private selectionId fingerprint identity shortName language kind =
        let kindValue =
            match kind with
            | WorkspaceCreateKind.Empty -> "empty"
            | WorkspaceCreateKind.ItemTemplate -> "item"
            | WorkspaceCreateKind.ProjectTemplate -> "project"
            | WorkspaceCreateKind.SolutionFolder -> "solution-folder"
            | WorkspaceCreateKind.AddExisting -> "add-existing"

        String.concat
            "\u001f"
            [ fingerprint
              identity
              shortName
              language |> Option.defaultValue String.Empty
              kindValue ]
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private templateHome () =
        Environment.GetEnvironmentVariable "DOTNET_CLI_HOME"
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () ->
            Environment.GetFolderPath Environment.SpecialFolder.UserProfile)

    let private readCacheAsync path (cancellationToken: CancellationToken) =
        task {
            try
                let! bytes = File.ReadAllBytesAsync(path, cancellationToken)
                return Found bytes
            with
            | :? FileNotFoundException
            | :? DirectoryNotFoundException -> return Missing
            | :? IOException
            | :? UnauthorizedAccessException -> return Unavailable
        }

    let private initializeCacheAsync path (cancellationToken: CancellationToken) =
        task {
            do! cacheInitialization.WaitAsync cancellationToken

            try
                match! readCacheAsync path cancellationToken with
                | Found bytes -> return Ok bytes
                | Unavailable -> return Error()
                | Missing ->
                    let! initialized =
                        DirectCommandRunner.ExecuteAsync(
                            [| "new"; "list" |],
                            Human(TextWriter.Null, TextWriter.Null, false, false),
                            cancellationToken
                        )

                    cancellationToken.ThrowIfCancellationRequested()

                    if not initialized.Success then
                        return Error()
                    else
                        match! readCacheAsync path cancellationToken with
                        | Found bytes -> return Ok bytes
                        | Missing
                        | Unavailable -> return Error()
            finally
                cacheInitialization.Release() |> ignore
        }

    let private parse fingerprint (document: JsonDocument) =
        match tryProperty "TemplateInfo" document.RootElement with
        | Some templates when templates.ValueKind = JsonValueKind.Array ->
            let candidates =
                templates.EnumerateArray()
                |> Seq.choose (fun template ->
                    match
                        kind template,
                        textProperty "Identity" template,
                        textProperty "Name" template,
                        shortName template
                    with
                    | Some templateKind, Some identity, Some name, Some templateShortName ->
                        let templateLanguage = language template

                        Some(
                            templateShortName,
                            templateLanguage,
                            templateKind,
                            integerProperty "Precedence" template,
                            identity,
                            name,
                            textProperty "Description" template |> Option.defaultValue name
                        )
                    | _ -> None)
                |> Seq.toArray

            let entries = ResizeArray<WorkspaceTemplateEntry>()
            let mutable ambiguity = None

            for _, group in
                candidates
                |> Seq.groupBy (fun (shortName, language, templateKind, _, _, _, _) ->
                    shortName.ToUpperInvariant(),
                    language |> Option.map _.ToUpperInvariant(),
                    templateKind) do
                let highest =
                    group
                    |> Seq.maxBy (fun (_, _, _, precedence, _, _, _) -> precedence)
                    |> fun (_, _, _, precedence, _, _, _) -> precedence

                let winners =
                    group
                    |> Seq.filter (fun (_, _, _, precedence, _, _, _) -> precedence = highest)
                    |> Seq.toArray

                let identities =
                    winners
                    |> Seq.map (fun (_, _, _, _, identity, _, _) -> identity)
                    |> Seq.distinct
                    |> Seq.toArray

                if identities.Length <> 1 then
                    let templateShortName, _, _, _, _, _, _ = winners[0]
                    ambiguity <- Some templateShortName
                else
                    let (templateShortName,
                         templateLanguage,
                         templateKind,
                         _,
                         identity,
                         name,
                         description) =
                        winners
                        |> Seq.sortBy (fun (_, _, _, _, candidateIdentity, _, _) ->
                            candidateIdentity)
                        |> Seq.head

                    entries.Add
                        { SelectionId =
                            selectionId
                                fingerprint
                                identity
                                templateShortName
                                templateLanguage
                                templateKind
                          Kind = templateKind
                          DisplayName = name
                          Description = description
                          Language = templateLanguage
                          Execution = WorkspaceCreateExecution.Operation
                          Identity = identity
                          ShortName = templateShortName
                          Fingerprint = fingerprint }

            match ambiguity with
            | Some templateShortName ->
                Error(
                    invalid
                        "template_catalog_ambiguous"
                        $"The active SDK has ambiguous '{templateShortName}' template registrations."
                )
            | None ->
                Ok(
                    entries
                    |> Seq.sortBy (fun entry ->
                        entry.Kind, entry.DisplayName, entry.Language, entry.Identity)
                    |> Seq.toArray
                )
        | _ ->
            Error(invalid "template_catalog_invalid" "The active SDK template catalog is invalid.")

    let parseBytes fingerprint (bytes: byte array) =
        try
            let json =
                if
                    bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy
                then
                    ReadOnlyMemory<byte>(bytes, 3, bytes.Length - 3)
                else
                    ReadOnlyMemory<byte>(bytes)

            use document = JsonDocument.Parse json
            parse fingerprint document
        with :? JsonException ->
            Error(invalid "template_catalog_invalid" "The active SDK template catalog is invalid.")

    let readAsync (workspace: SolutionWorkspace) (cancellationToken: CancellationToken) =
        task {
            let executable =
                Environment.GetEnvironmentVariable "DOTNET_HOST_PATH"
                |> Option.ofObj
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.defaultValue "dotnet"

            let! selected =
                DotnetSdkResolver.DiscoverAsync(
                    workspace.SolutionPath,
                    executable,
                    cancellationToken
                )

            match selected with
            | Failure failure -> return Error(WorkspaceRpcResponses.failureError failure)
            | Success selection ->
                let version =
                    Path.GetFileName selection.SdkPath.Value
                    |> Option.ofObj
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)

                match version with
                | None ->
                    return
                        Error(
                            invalid
                                "template_catalog_unavailable"
                                "The active workspace SDK version is unavailable."
                        )
                | Some version ->
                    let path =
                        Path.Combine(
                            templateHome (),
                            ".templateengine",
                            "dotnetcli",
                            version,
                            "templatecache.json"
                        )

                    try
                        let! bytes =
                            task {
                                match! readCacheAsync path cancellationToken with
                                | Found bytes -> return Ok bytes
                                | Missing -> return! initializeCacheAsync path cancellationToken
                                | Unavailable -> return Error()
                            }

                        match bytes with
                        | Error() ->
                            return
                                Error(
                                    invalid
                                        "template_catalog_unavailable"
                                        "The active SDK template catalog could not be read."
                                )
                        | Ok bytes ->
                            let fingerprint = SHA256.HashData bytes |> Convert.ToHexString

                            return
                                parseBytes fingerprint bytes
                                |> Result.map (fun entries ->
                                    { Fingerprint = fingerprint
                                      EmptySelectionId =
                                        selectionId
                                            fingerprint
                                            "workspace.empty"
                                            "empty"
                                            None
                                            WorkspaceCreateKind.Empty
                                      Entries = entries })
                    with :? OperationCanceledException ->
                        return Error(invalid "cancelled" "Template discovery was cancelled.")
        }

    let find suppliedSelectionId (catalog: WorkspaceTemplateCatalog) =
        let logical kind identity shortName displayName description =
            let id = selectionId catalog.Fingerprint identity shortName None kind

            if suppliedSelectionId = id then
                Some
                    { SelectionId = id
                      Kind = kind
                      DisplayName = displayName
                      Description = description
                      Language = None
                      Execution = WorkspaceCreateExecution.Transaction
                      Identity = identity
                      ShortName = shortName
                      Fingerprint = catalog.Fingerprint }
            else
                None

        if suppliedSelectionId = catalog.EmptySelectionId then
            Some
                { SelectionId = catalog.EmptySelectionId
                  Kind = WorkspaceCreateKind.Empty
                  DisplayName = "Empty file"
                  Description = "Create an empty project file"
                  Language = None
                  Execution = WorkspaceCreateExecution.Transaction
                  Identity = "workspace.empty"
                  ShortName = "empty"
                  Fingerprint = catalog.Fingerprint }
        else
            logical
                WorkspaceCreateKind.SolutionFolder
                "workspace.solution-folder"
                "solution-folder"
                "Solution Folder"
                "Create a logical solution folder"
            |> Option.orElseWith (fun () ->
                logical
                    WorkspaceCreateKind.AddExisting
                    "workspace.add-existing"
                    "add-existing"
                    "Add Existing"
                    "Add existing projects or files")
            |> Option.orElseWith (fun () ->
                catalog.Entries
                |> Array.tryFind (fun entry -> entry.SelectionId = suppliedSelectionId))

    let binding (entry: WorkspaceTemplateEntry) =
        { SelectionId = entry.SelectionId
          Fingerprint = entry.Fingerprint
          Identity = entry.Identity
          ShortName = entry.ShortName
          Kind = entry.Kind
          Language = entry.Language }

    let validateBinding (expected: WorkspaceTemplateBinding) (catalog: WorkspaceTemplateCatalog) =
        match find expected.SelectionId catalog with
        | Some entry when
            catalog.Fingerprint = expected.Fingerprint
            && entry.Identity = expected.Identity
            && entry.ShortName = expected.ShortName
            && entry.Kind = expected.Kind
            && entry.Language = expected.Language
            ->
            Ok()
        | _ ->
            Error(
                invalid
                    "template_catalog_changed"
                    "The selected template registration changed before execution."
            )

    let projectLanguage (projectPath: WorkspaceArtifactPath option) =
        projectPath
        |> Option.bind (fun path ->
            match
                Path.GetExtension(path.Value)
                |> Option.ofObj
                |> Option.defaultValue String.Empty
                |> _.ToLowerInvariant()
            with
            | ".csproj" -> Some "C#"
            | ".fsproj" -> Some "F#"
            | ".vbproj" -> Some "VB"
            | _ -> None)

    let options
        (context: WorkspaceSemanticContext)
        addExistingNegotiated
        (catalog: WorkspaceTemplateCatalog)
        =
        let projectLanguage = projectLanguage context.ProjectPath
        let hasProject = context.ProjectId.IsSome

        seq {
            if
                context.Node.Kind = WorkspaceNodeKind.Workspace
                || context.Node.Kind = WorkspaceNodeKind.SolutionFolder
            then
                yield
                    find
                        (selectionId
                            catalog.Fingerprint
                            "workspace.solution-folder"
                            "solution-folder"
                            None
                            WorkspaceCreateKind.SolutionFolder)
                        catalog
                    |> Option.get

            if
                addExistingNegotiated
                && (context.Node.Kind = WorkspaceNodeKind.Workspace
                    || context.Node.Kind = WorkspaceNodeKind.SolutionFolder
                    || context.Node.Kind = WorkspaceNodeKind.Project
                    || context.Node.Kind = WorkspaceNodeKind.ProjectFolder)
            then
                yield
                    find
                        (selectionId
                            catalog.Fingerprint
                            "workspace.add-existing"
                            "add-existing"
                            None
                            WorkspaceCreateKind.AddExisting)
                        catalog
                    |> Option.get

            if hasProject then
                yield
                    find catalog.EmptySelectionId catalog
                    |> Option.defaultWith (fun () ->
                        invalidOp "The empty selection is unavailable.")

            yield!
                catalog.Entries
                |> Seq.filter (fun entry ->
                    match entry.Kind with
                    | WorkspaceCreateKind.ProjectTemplate -> true
                    | WorkspaceCreateKind.ItemTemplate when hasProject ->
                        entry.Language.IsNone || entry.Language = projectLanguage
                    | _ -> false)
        }
        |> Seq.toArray
