namespace Dotnet.CLI.Plus

#nowarn "3261"
#nowarn "3511"

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Xml
open System.Xml.Linq
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution

type internal CommandCompatibility =
    { PlusGrammar: string
      ChildArguments: string
      PassThroughOptions: string
      UnsupportedCases: string }

module internal CompatibilityTable =
    let Commands =
        [ { PlusGrammar = "[--json] solution|sln [<SLN_FILE>] add|list|remove|migrate [options]"
            ChildArguments = "solution ...; sln is normalized"
            PassThroughOptions = "All child argv, including tokens after --, is preserved exactly."
            UnsupportedCases = "Mutating a selected .slnf; unexpandable solution globs." }
          { PlusGrammar = "[--json] package add|list|remove|update|search|download [options]"
            ChildArguments = "package ..."
            PassThroughOptions = "SDK options and their values can appear before operands."
            UnsupportedCases = "Ambiguous current-directory project selection." }
          { PlusGrammar = "[--json] reference add|list|remove [options]"
            ChildArguments = "reference ..."
            PassThroughOptions = "SDK options and their values can appear before operands."
            UnsupportedCases = "Ambiguous current-directory project selection." }
          { PlusGrammar = "[--json] new [template|create|list|search|details|install|uninstall|update] [options]"
            ChildArguments = "new ..."
            PassThroughOptions = "--output/-o and --dry-run are inspected without changing child argv."
            UnsupportedCases = "Template state that cannot be deterministically refreshed." }
          { PlusGrammar = "[--json] restore|build|test|run [options]"
            ChildArguments = "same command and arguments"
            PassThroughOptions = "All child argv is preserved exactly."
            UnsupportedCases = "Lifecycle policy and orchestration (T-011)." } ]

type private SolutionOperation =
    | Add
    | List
    | Remove
    | Migrate

type private PackageOperation =
    | PackageAdd
    | PackageList
    | PackageRemove
    | PackageUpdate
    | PackageSearch
    | PackageDownload

type private ReferenceOperation =
    | ReferenceAdd
    | ReferenceList
    | ReferenceRemove

type private NewOperation =
    | TemplateCreate
    | TemplateList
    | TemplateSearch
    | TemplateDetails
    | TemplateInstall
    | TemplateUninstall
    | TemplateUpdate

type private ParsedCommand =
    | Solution of target: string option * operation: SolutionOperation option * operands: string list * help: bool
    | Package of
        operation: PackageOperation option *
        project: string option *
        file: string option *
        version: string option *
        operands: string list *
        help: bool
    | Reference of operation: ReferenceOperation option * project: string option * operands: string list * help: bool
    | New of operation: NewOperation * output: string option * dryRun: bool * operands: string list * help: bool
    | Lifecycle of command: string * help: bool

type internal BrokerHost =
    { FileName: string
      Prefix: string list }

type internal BrokerMode =
    | Human of TextWriter * TextWriter * bool * bool
    | Json

type internal BrokerPayload =
    { Summary: string option
      ChildArguments: string list
      StandardOutput: string
      StandardError: string }

type internal BrokerResult =
    { CommandId: string
      Success: bool
      Revision: WorkspaceRevision option
      Payload: BrokerPayload
      Diagnostics: WorkspaceDiagnostic list
      ExternalExitCode: int option }

type internal IncrementalTerminalSanitizer() =
    let pending = StringBuilder()

    member _.Push(value: string) =
        pending.Append(value) |> ignore
        let source = pending.ToString()
        let output = StringBuilder()
        let mutable index = 0
        let mutable incomplete = -1

        while index < source.Length && incomplete < 0 do
            let character = source[index]

            if character = '\u001b' then
                if index + 1 >= source.Length then
                    incomplete <- index
                elif source[index + 1] = '[' then
                    let mutable endIndex = index + 2

                    while endIndex < source.Length
                          && not (source[endIndex] >= '@' && source[endIndex] <= '~') do
                        endIndex <- endIndex + 1

                    if endIndex = source.Length then
                        incomplete <- index
                    else
                        index <- endIndex + 1
                elif source[index + 1] = ']' then
                    let mutable endIndex = index + 2
                    let mutable found = false

                    while endIndex < source.Length && not found do
                        found <-
                            source[endIndex] = '\u0007'
                            || (source[endIndex] = '\u001b'
                                && endIndex + 1 < source.Length
                                && source[endIndex + 1] = '\\')

                        endIndex <- endIndex + 1

                    if not found then
                        incomplete <- index
                    else
                        index <- endIndex + (if source[endIndex - 1] = '\u001b' then 1 else 0)
                else
                    index <- index + 2
            else
                if
                    character = '\t'
                    || character = '\n'
                    || character = '\r'
                    || (character >= ' ' && character <> '\u007f')
                then
                    output.Append(character) |> ignore

                index <- index + 1

        pending.Clear() |> ignore

        if incomplete >= 0 then
            pending.Append(source.Substring(incomplete)) |> ignore

        output.ToString()

    member _.Complete() =
        pending.Clear() |> ignore
        String.Empty

module private Failure =
    let diagnostic code message retryable =
        WorkspaceDiagnostic.CreateSimple(
            WorkspaceDiagnosticSeverity.Error,
            WorkspaceDiagnosticCode.Create code,
            message,
            retryable,
            CorrelationId.New()
        )

    let invalid message =
        InvalidInput("arguments", diagnostic WorkspaceErrorCode.InvalidInput.Value message false)

    let unsupported message =
        UnsupportedCapability(
            WorkspaceCapabilityId.Write,
            diagnostic WorkspaceErrorCode.UnsupportedCapability.Value message false
        )

    let external exitCode =
        ExternalToolFailed(
            "dotnet",
            exitCode,
            diagnostic WorkspaceErrorCode.ExternalToolFailed.Value "The dotnet command failed." true
        )

    let verification message =
        Internal(diagnostic WorkspaceErrorCode.InternalError.Value message false)

    let cancelled () =
        Cancelled(
            OperationId.New(),
            diagnostic WorkspaceErrorCode.Cancelled.Value "The dotnet command was cancelled." true
        )

    let internalFailure message =
        Internal(diagnostic WorkspaceErrorCode.InternalError.Value message false)

module private Grammar =
    let private help tokens =
        tokens
        |> List.exists (fun token -> token = "--help" || token = "-h" || token = "-?")

    let private splitSentinel tokens =
        match tokens |> List.tryFindIndex ((=) "--") with
        | Some index -> tokens |> List.take index, tokens |> List.skip index
        | None -> tokens, []

    let private scan (values: Set<string>) (flags: Set<string>) (tokens: string list) =
        let rec collect
            (remaining: string list)
            (collected: Map<string, string>)
            (positional: string list)
            (unknown: string list)
            =
            match remaining with
            | [] -> collected, List.rev positional, List.rev unknown
            | ("--help" | "-h" | "-?") :: tail -> collect tail collected positional unknown
            | token :: tail when
                token.StartsWith("--", StringComparison.Ordinal)
                && token.Contains("=", StringComparison.Ordinal)
                ->
                let name, value = token.Split('=', 2) |> fun parts -> parts[0], parts[1]

                if values |> Set.contains name then
                    collect tail (Map.add name value collected) positional unknown
                elif flags |> Set.contains name then
                    collect tail collected positional unknown
                else
                    collect tail collected positional (name :: unknown)
            | token :: value :: tail when values |> Set.contains token ->
                collect tail (Map.add token value collected) positional unknown
            | token :: tail when flags |> Set.contains token -> collect tail collected positional unknown
            | token :: tail when token.StartsWith("-", StringComparison.Ordinal) ->
                collect tail collected positional (token :: unknown)
            | token :: tail -> collect tail collected (token :: positional) unknown

        collect tokens Map.empty [] []

    let private scanSolution =
        scan (Set.ofList [ "--solution-folder"; "-s" ]) (Set.ofList [ "--in-root"; "--include-references" ])

    let private scanPackage =
        scan
            (Set.ofList
                [ "--project"
                  "--file"
                  "--version"
                  "--framework"
                  "--source"
                  "--configfile"
                  "--package-directory"
                  "--verbosity" ])
            (Set.ofList [ "--prerelease"; "--no-restore"; "--interactive" ])

    let private scanReference =
        scan (Set.ofList [ "--project"; "--framework" ]) (Set.ofList [ "--interactive" ])

    let private scanNew =
        scan
            (Set.ofList
                [ "--output"
                  "-o"
                  "--name"
                  "-n"
                  "--project"
                  "--verbosity"
                  "-v"
                  "--add-source"
                  "--nuget-source" ])
            (Set.ofList [ "--dry-run"; "--check-only"; "--force"; "--no-update-check"; "--diagnostics" ])

    let parse (arguments: string array) =
        let json, child =
            match arguments |> Array.toList with
            | "--json" :: tail -> true, tail
            | tail -> false, tail

        let beforeSentinel, sentinel = splitSentinel child

        let sentinelOperands =
            match sentinel with
            | [] -> []
            | _ :: tail -> tail

        let parsed =
            match child with
            | [] -> Error(Failure.invalid "A plus command is required.")
            | command :: rest ->
                match command with
                | "solution"
                | "sln" ->
                    let _, positions, unknown = scanSolution beforeSentinel.Tail

                    let operationIndex =
                        positions
                        |> List.tryFindIndex (fun value ->
                            [ "add"; "list"; "remove"; "migrate" ] |> List.contains value)

                    let target, operation, operands =
                        match operationIndex with
                        | Some index ->
                            let before = positions |> List.take index
                            let after = positions |> List.skip (index + 1)
                            let target = if before.Length = 1 then Some before.Head else None

                            let operation =
                                [ Add; List; Remove; Migrate ][positions[index]
                                                               |> function
                                                                   | "add" -> 0
                                                                   | "list" -> 1
                                                                   | "remove" -> 2
                                                                   | _ -> 3]

                            target, Some operation, after @ sentinelOperands
                        | None ->
                            match positions with
                            | [ target ] -> Some target, None, []
                            | _ -> None, None, []

                    if
                        not (List.isEmpty unknown)
                        && operation |> Option.exists (fun value -> value <> List)
                    then
                        Error(Failure.invalid "Unknown solution option prevents verification.")
                    else
                        Ok(Solution(target, operation, operands, help beforeSentinel))
                | "package" ->
                    let options, positions, unknown = scanPackage beforeSentinel.Tail

                    let operation, operands =
                        match positions with
                        | op :: tail ->
                            let parsed =
                                match op with
                                | "add" -> Some PackageAdd
                                | "list" -> Some PackageList
                                | "remove" -> Some PackageRemove
                                | "update" -> Some PackageUpdate
                                | "search" -> Some PackageSearch
                                | "download" -> Some PackageDownload
                                | _ -> None

                            parsed, tail @ sentinelOperands
                        | [] -> None, []

                    if
                        not (List.isEmpty unknown)
                        && operation
                           |> Option.exists (fun value ->
                               value = PackageAdd || value = PackageRemove || value = PackageUpdate)
                    then
                        Error(Failure.invalid "Unknown package option prevents verification.")
                    else
                        Ok(
                            Package(
                                operation,
                                options |> Map.tryFind "--project",
                                options |> Map.tryFind "--file",
                                options |> Map.tryFind "--version",
                                operands,
                                help beforeSentinel
                            )
                        )
                | "reference" ->
                    let options, positions, unknown = scanReference beforeSentinel.Tail

                    let operation, operands =
                        match positions with
                        | op :: tail ->
                            let parsed =
                                match op with
                                | "add" -> Some ReferenceAdd
                                | "list" -> Some ReferenceList
                                | "remove" -> Some ReferenceRemove
                                | _ -> None

                            parsed, tail @ sentinelOperands
                        | [] -> None, []

                    if
                        not (List.isEmpty unknown)
                        && operation
                           |> Option.exists (fun value -> value = ReferenceAdd || value = ReferenceRemove)
                    then
                        Error(Failure.invalid "Unknown reference option prevents verification.")
                    else
                        Ok(Reference(operation, options |> Map.tryFind "--project", operands, help beforeSentinel))
                | "new" ->
                    let options, positions, unknown = scanNew beforeSentinel.Tail

                    let operation, operands =
                        match positions with
                        | "list" :: tail -> TemplateList, tail
                        | "search" :: tail -> TemplateSearch, tail
                        | "details" :: tail -> TemplateDetails, tail
                        | "install" :: tail -> TemplateInstall, tail
                        | "uninstall" :: tail -> TemplateUninstall, tail
                        | "update" :: tail -> TemplateUpdate, tail
                        | "create" :: tail -> TemplateCreate, tail
                        | tail -> TemplateCreate, tail @ sentinelOperands

                    Ok(
                        New(
                            operation,
                            options
                            |> Map.tryFind "--output"
                            |> Option.orElseWith (fun () -> options |> Map.tryFind "-o"),
                            beforeSentinel
                            |> List.exists (fun value -> value = "--dry-run" || value = "--check-only"),
                            operands,
                            help beforeSentinel
                        )
                    )
                | "restore"
                | "build"
                | "test"
                | "run" -> Ok(Lifecycle(command, help beforeSentinel))
                | _ -> Error(Failure.invalid "This dotnet command is not in the dotnet-plus compatibility grammar.")

        json, child, parsed

    let commandId =
        function
        | Solution _ -> "solution"
        | Package _ -> "package"
        | Reference _ -> "reference"
        | New _ -> "new"
        | Lifecycle(command, _) -> command

    let mutates =
        function
        | Solution(_, Some(Add | Remove | Migrate), _, false) -> true
        | Package(Some(PackageAdd | PackageRemove | PackageUpdate), _, _, _, _, false) -> true
        | Reference(Some(ReferenceAdd | ReferenceRemove), _, _, false) -> true
        | New(TemplateCreate, _, false, _, false) -> true
        | _ -> false

module private Paths =
    let projects (directory: string) =
        Directory.EnumerateFiles(directory, "*.*proj", SearchOption.TopDirectoryOnly)
        |> Seq.filter (fun path -> [ ".csproj"; ".fsproj"; ".vbproj" ] |> List.contains (Path.GetExtension path))
        |> Seq.sort
        |> Seq.toList

    let defaultProject () =
        match projects (Directory.GetCurrentDirectory()) with
        | [ project ] -> Ok project
        | [] -> Error "No project exists in the current directory."
        | _ -> Error "More than one project exists in the current directory; use --project."

    let expandSolutionOperand (operand: string) =
        if operand.IndexOfAny([| '*'; '?' |]) >= 0 then
            let full = Path.GetFullPath operand
            let segments = full.Replace('\\', '/').Split('/')

            let wildcard =
                segments
                |> Array.findIndex (fun segment -> segment.IndexOfAny([| '*'; '?' |]) >= 0)

            let prefix = segments |> Array.take wildcard |> String.concat "/"

            let root =
                if String.IsNullOrEmpty prefix then
                    Path.DirectorySeparatorChar.ToString()
                else
                    prefix

            let expression =
                "^"
                + Regex
                    .Escape(full.Replace('\\', '/'))
                    .Replace("\\*\\*", ".*")
                    .Replace("\\*", "[^/]*")
                    .Replace("\\?", "[^/]")
                + "$"

            let matcher = Regex(expression, RegexOptions.CultureInvariant)

            if Directory.Exists root then
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                |> Seq.filter (fun path -> matcher.IsMatch(path.Replace('\\', '/')))
                |> Seq.toList
            else
                []
        else
            [ operand ]

type internal FileBasedPackageDirective = { Id: string; Version: string option }

module internal FileBasedPackageDirectives =
    let private directive =
        Regex("^\\s*#:\\s*package\\s+([^@\\s]+)(?:@([^\\s]+))?\\s*$", RegexOptions.CultureInvariant)

    let private prefix = Regex("^\\s*#:\\s*package\\b", RegexOptions.CultureInvariant)

    let Parse (source: string) =
        if isNull source then
            Error(Failure.invalid "Package source text is required.")
        else
            source.Replace("\r\n", "\n").Split '\n'
            |> Array.fold
                (fun state line ->
                    match state with
                    | Error failure -> Error failure
                    | Ok directives ->
                        let matched = directive.Match line

                        if matched.Success then
                            let version = matched.Groups[2].Value

                            Ok(
                                { Id = matched.Groups[1].Value
                                  Version =
                                    if String.IsNullOrWhiteSpace version then
                                        None
                                    else
                                        Some version }
                                :: directives
                            )
                        elif prefix.IsMatch line then
                            Error(Failure.invalid "A file-based package directive is malformed.")
                        else
                            Ok directives)
                (Ok [])
            |> Result.map List.rev

    let Contains (id: string, version: string option, directives: FileBasedPackageDirective list) =
        directives
        |> List.exists (fun directive ->
            String.Equals(directive.Id, id, StringComparison.OrdinalIgnoreCase)
            && (version |> Option.forall (fun expected -> directive.Version = Some expected)))

type internal TemplateEngineState =
    { Packages: string list
      Mounts: string list }

module internal TemplateEngineStateReader =
    let Root () =
        Environment.GetEnvironmentVariable "DOTNET_CLI_HOME"
        |> Option.ofObj
        |> Option.defaultValue (Environment.GetFolderPath Environment.SpecialFolder.UserProfile)
        |> fun home -> Path.Combine(home, ".templateengine")

    let Read (root: string) =
        try
            let caches =
                if Directory.Exists root then
                    Directory.EnumerateFiles(root, "templatecache.json", SearchOption.AllDirectories)
                    |> Seq.toList
                else
                    []

            let values =
                caches
                |> List.collect (fun cache ->
                    use document = JsonDocument.Parse(File.ReadAllText cache)
                    let mutable mounts = Unchecked.defaultof<JsonElement>

                    if document.RootElement.TryGetProperty("MountPointsInfo", &mounts) then
                        mounts.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                    else
                        [])

            Ok { Packages = values; Mounts = values }
        with
        | :? JsonException -> Error(Failure.invalid "The template cache is malformed.")
        | :? IOException -> Error(Failure.internalFailure "The template cache could not be read.")

    let Contains (subject: string, state: TemplateEngineState) =
        let id = subject.Split("::", 2)[0]

        state.Packages
        |> List.exists (fun value ->
            let name = Path.GetFileNameWithoutExtension value in

            String.Equals(name, id, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase)
            || String.Equals(value, subject, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(id + "::", StringComparison.OrdinalIgnoreCase))

module private Verify =
    let private openSolution target cancellationToken =
        task {
            let! outcome =
                SolutionStore.OpenAsync(
                    target |> Option.defaultValue (Directory.GetCurrentDirectory()),
                    cancellationToken
                )

            return
                match outcome with
                | Success workspace -> Ok workspace
                | Failure failure -> Error failure
        }

    let prepareSolution
        (target: string option)
        (operation: SolutionOperation)
        (operands: string list)
        cancellationToken
        =
        task {
            if
                (operation = Add || operation = Remove)
                && (List.isEmpty operands
                    || (operands
                        |> List.exists (fun operand ->
                            operand.IndexOfAny([| '*'; '?' |]) >= 0
                            && List.isEmpty (Paths.expandSolutionOperand operand))))
            then
                return Error(Failure.invalid "Solution add/remove requires one or more matching project operands.")
            else
                match target with
                | Some path when path.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase) ->
                    return Error(Failure.unsupported ".slnf workspaces are read-only and cannot be mutated.")
                | _ ->
                    let! workspace = openSolution target cancellationToken

                    return
                        match workspace with
                        | Error failure -> Error failure
                        | Ok workspace when workspace.WorkspaceDescriptor.IsReadOnly ->
                            Error(Failure.unsupported ".slnf workspaces are read-only and cannot be mutated.")
                        | Ok workspace -> Ok workspace
        }

    let private solutionProjects (workspace: SolutionWorkspace) =
        workspace.RootProjection.Projects
        |> Seq.map (fun project -> project.Node.Name, project.Path.AbsolutePath.Value)
        |> Seq.toList

    let private requestedSolutionOperands operands =
        let expanded = operands |> List.collect Paths.expandSolutionOperand

        if List.isEmpty operands || List.isEmpty expanded then
            Error "Solution add/remove requires at least one verifiable project operand."
        else
            Ok expanded

    let verifySolution target operation operands cancellationToken =
        task {
            let! opened = openSolution target cancellationToken

            match opened with
            | Error failure -> return Error failure
            | Ok workspace ->
                let pathComparer =
                    match HostFileSystemCaseDetector.DetectFromExistingPath(workspace.BackingPath.Value) with
                    | HostFileSystemCaseSemantics.Insensitive -> StringComparer.OrdinalIgnoreCase
                    | _ -> StringComparer.Ordinal

                match operation with
                | Some Add
                | Some Remove ->
                    match requestedSolutionOperands operands with
                    | Error message -> return Error(Failure.invalid message)
                    | Ok requested ->
                        let projects = solutionProjects workspace

                        let matches operand =
                            projects
                            |> List.exists (fun (name, path) ->
                                String.Equals(name, operand, StringComparison.OrdinalIgnoreCase)
                                || pathComparer.Equals(path, Path.GetFullPath operand))

                        let correct =
                            match operation with
                            | Some Add -> requested |> List.forall matches
                            | _ -> requested |> List.forall (matches >> not)

                        if correct then
                            return Ok(Some workspace.WorkspaceDescriptor.WorkspaceRevision)
                        else
                            return
                                Error(
                                    Failure.verification
                                        "The refreshed solution does not contain the requested final project state."
                                )
                | Some Migrate ->
                    let migrated = Path.ChangeExtension(workspace.BackingPath.Value, ".slnx")

                    if File.Exists migrated then
                        return Ok(Some workspace.WorkspaceDescriptor.WorkspaceRevision)
                    else
                        return Error(Failure.verification "The migrated .slnx file was not created.")
                | _ -> return Ok(Some workspace.WorkspaceDescriptor.WorkspaceRevision)
        }

    let private descendants name (document: XDocument) =
        document.Descendants()
        |> Seq.filter (fun element -> element.Name.LocalName = name)

    let private attribute name (element: XElement) =
        element.Attribute(XName.Get name) |> Option.ofObj |> Option.map _.Value

    let packageSubject (value: string) =
        let index = value.LastIndexOf '@'

        if index > 0 then
            value.Substring(0, index), Some(value.Substring(index + 1))
        else
            value, None

    let private centralVersion (project: string) (id: string) =
        let rec find directory =
            let candidate = Path.Combine(directory, "Directory.Packages.props")

            if File.Exists candidate then
                let document = XDocument.Load candidate

                descendants "PackageVersion" document
                |> Seq.tryFind (fun element ->
                    attribute "Include" element
                    |> Option.orElseWith (fun () -> attribute "Update" element)
                    |> Option.exists (fun value -> String.Equals(value, id, StringComparison.OrdinalIgnoreCase)))
                |> Option.bind (fun element ->
                    attribute "Version" element
                    |> Option.orElseWith (fun () ->
                        element.Elements()
                        |> Seq.tryFind (fun child -> child.Name.LocalName = "Version")
                        |> Option.map _.Value))
            else
                match Directory.GetParent directory with
                | null -> None
                | parent -> find parent.FullName

        Path.GetDirectoryName project |> Option.ofObj |> Option.bind find

    let verifyPackage operation (project: string) operands =
        match operands with
        | [] -> Error(Failure.invalid "Package mutations require a package ID.")
        | subjects ->
            let document = XDocument.Load project

            let comparer =
                match HostFileSystemCaseDetector.DetectFromExistingPath project with
                | HostFileSystemCaseSemantics.Insensitive -> StringComparer.OrdinalIgnoreCase
                | _ -> StringComparer.Ordinal

            let references = descendants "PackageReference" document |> Seq.toList

            let present subject =
                let id, version = packageSubject subject

                references
                |> List.exists (fun reference ->
                    let matchesId =
                        attribute "Include" reference
                        |> Option.orElseWith (fun () -> attribute "Update" reference)
                        |> Option.exists (fun actual -> String.Equals(actual, id, StringComparison.OrdinalIgnoreCase))

                    let actualVersion =
                        attribute "Version" reference
                        |> Option.orElseWith (fun () ->
                            reference.Elements()
                            |> Seq.tryFind (fun child -> child.Name.LocalName = "Version")
                            |> Option.map _.Value)

                    let effectiveVersion =
                        actualVersion |> Option.orElseWith (fun () -> centralVersion project id)

                    matchesId
                    && (version |> Option.forall (fun expected -> effectiveVersion = Some expected)))

            let correct =
                match operation with
                | PackageAdd
                | PackageUpdate -> subjects |> List.forall present
                | PackageRemove -> subjects |> List.forall (present >> not)
                | _ -> true

            if correct then
                Ok None
            else
                Error(Failure.verification "The refreshed project does not contain the requested package state.")

    let verifyReferences operation (project: string) operands =
        if List.isEmpty operands then
            Error(Failure.invalid "Reference mutations require one or more project operands.")
        else
            let projectDirectory =
                Path.GetDirectoryName project
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let document = XDocument.Load project

            let comparer =
                match HostFileSystemCaseDetector.DetectFromExistingPath project with
                | HostFileSystemCaseSemantics.Insensitive -> StringComparer.OrdinalIgnoreCase
                | _ -> StringComparer.Ordinal

            let references =
                descendants "ProjectReference" document
                |> Seq.choose (attribute "Include")
                |> Seq.map (fun value -> Path.GetFullPath(value, projectDirectory))
                |> Seq.toList

            let requested =
                operands |> List.map (fun value -> Path.GetFullPath(value, projectDirectory))

            let correct =
                match operation with
                | ReferenceAdd ->
                    requested
                    |> List.forall (fun value ->
                        references |> List.exists (fun reference -> comparer.Equals(reference, value)))
                | ReferenceRemove ->
                    requested
                    |> List.forall (fun value ->
                        references
                        |> List.exists (fun reference -> comparer.Equals(reference, value))
                        |> not)
                | _ -> true

            if correct then
                Ok None
            else
                Error(Failure.verification "The refreshed project does not contain the requested reference state.")

    let snapshot (directory: string) =
        if Directory.Exists directory then
            Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            |> Seq.map (fun path ->
                let info = FileInfo path
                path, (info.Length, info.LastWriteTimeUtc.Ticks))
            |> Map.ofSeq
        else
            Map.empty

    let verifyNew (output: string) before =
        let after = snapshot output

        if after <> before then
            Ok None
        else
            Error(Failure.verification "The template command did not create a verifiable output state.")

module private ProcessExecution =
    let private ansi =
        Regex("\u001b(?:[@-_][0-?]*[ -/]*[@-~]|\\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled)

    let sanitize value =
        ansi.Replace(value, String.Empty)
        |> Seq.filter (fun character ->
            character = '\t'
            || character = '\n'
            || character = '\r'
            || (character >= ' ' && character <> '\u007f'))
        |> String.Concat

    let private pump (reader: StreamReader) (writer: TextWriter) tty capture =
        task {
            let builder = StringBuilder()
            let buffer = Array.zeroCreate<char> 1024
            let sanitizer = if tty then None else Some(IncrementalTerminalSanitizer())

            let rec copy () =
                task {
                    let! read = reader.ReadAsync(buffer, 0, buffer.Length)

                    if read > 0 then
                        let chunk = String(buffer, 0, read)
                        builder.Append chunk |> ignore

                        writer.Write(
                            match sanitizer with
                            | Some value -> value.Push chunk
                            | None -> chunk
                        )

                        writer.Flush()
                        return! copy ()
                }

            do! copy ()

            sanitizer
            |> Option.iter (fun value ->
                writer.Write(value.Complete())
                writer.Flush())

            return builder.ToString()
        }

    let run host childArguments mode cancellationToken =
        task {
            let info =
                ProcessStartInfo(
                    FileName = host.FileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                )

            host.Prefix |> List.iter info.ArgumentList.Add
            childArguments |> List.iter info.ArgumentList.Add
            use childProcess = new Process(StartInfo = info)

            try
                if not (childProcess.Start()) then
                    return Error(Failure.internalFailure "The dotnet host did not start.")
                else
                    let outputTask, errorTask =
                        match mode with
                        | Json ->
                            childProcess.StandardOutput.ReadToEndAsync(), childProcess.StandardError.ReadToEndAsync()
                        | Human(output, error, outputIsTty, errorIsTty) ->
                            pump childProcess.StandardOutput output outputIsTty true,
                            pump childProcess.StandardError error errorIsTty true

                    let! wasCancelled =
                        task {
                            try
                                do! childProcess.WaitForExitAsync cancellationToken
                                return false
                            with :? OperationCanceledException ->
                                return true
                        }

                    if wasCancelled then
                        try
                            if not childProcess.HasExited then
                                childProcess.Kill(true)
                        with
                        | :? InvalidOperationException
                        | :? System.ComponentModel.Win32Exception ->
                            try
                                if not childProcess.HasExited then
                                    childProcess.Kill()
                            with
                            | :? InvalidOperationException
                            | :? System.ComponentModel.Win32Exception -> ()

                        do! childProcess.WaitForExitAsync CancellationToken.None
                        let! output = outputTask
                        let! error = errorTask
                        return Error(Failure.cancelled ())
                    else
                        let! output = outputTask
                        let! error = errorTask
                        return Ok(childProcess.ExitCode, output, error)
            with
            | :? OperationCanceledException -> return Error(Failure.cancelled ())
            | :? System.ComponentModel.Win32Exception ->
                return Error(Failure.internalFailure "The dotnet host could not be started.")
        }

module internal Broker =
    let private productionHost () =
        { FileName =
            Environment.GetEnvironmentVariable "DOTNET_HOST_PATH"
            |> Option.ofObj
            |> Option.defaultValue "dotnet"
          Prefix = [] }

    let private result command success revision diagnostics externalExitCode child output error =
        { CommandId = command
          Success = success
          Revision = revision
          Diagnostics = diagnostics
          ExternalExitCode = externalExitCode
          Payload =
            { Summary = if success then Some "dotnet command completed" else None
              ChildArguments = child
              StandardOutput = output
              StandardError = error } }

    let private failed command (failure: WorkspaceFailure) exit child output error =
        result command false None [ failure.Diagnostic ] exit child output error

    let private legacyDirectoryAdd raw cancellationToken =
        task {
            match raw with
            | ("solution" | "sln") :: solutionPath :: "add" :: ("directory" | "dir") :: directoryPath :: [] ->
                let! legacy =
                    LegacySolutionCompatibilityEditor.AddDirectoryAsync(solutionPath, directoryPath, cancellationToken)

                if legacy.ExitCode <> 0 then
                    return Some(failed "solution" (Failure.external legacy.ExitCode) (Some legacy.ExitCode) [] "" "")
                else
                    let! refreshed = Verify.verifySolution (Some solutionPath) None [] cancellationToken

                    return
                        match refreshed with
                        | Ok revision -> Some(result "solution" true revision [] (Some 0) [] "" "")
                        | Error failure -> Some(failed "solution" failure (Some 0) [] "" "")
            | _ -> return None
        }

    let private executeCore arguments host mode cancellationToken =
        task {
            let _, raw, parsed = Grammar.parse arguments

            let! legacy = legacyDirectoryAdd raw cancellationToken

            match legacy, parsed with
            | Some result, _ -> return result
            | None, Error failure -> return failed "" failure None [] "" ""
            | None, Ok command ->
                let commandId = Grammar.commandId command

                let child =
                    match raw with
                    | "sln" :: tail -> "solution" :: tail
                    | _ -> raw

                let! prepared =
                    task {
                        match command with
                        | Solution(target, Some(operation as (Add | Remove | Migrate)), operands, false) ->
                            let! workspace = Verify.prepareSolution target operation operands cancellationToken
                            return workspace |> Result.map ignore
                        | _ -> return Ok()
                    }

                match prepared with
                | Error failure -> return failed commandId failure None child "" ""
                | Ok _ ->
                    let newOutput, before =
                        match command with
                        | New(TemplateCreate, output, false, _, false) ->
                            let target = output |> Option.defaultValue (Directory.GetCurrentDirectory()) in
                            target, Verify.snapshot target
                        | _ -> "", Map.empty

                    let! executed = ProcessExecution.run host child mode cancellationToken

                    match executed with
                    | Error failure -> return failed commandId failure None child "" ""
                    | Ok(exitCode, output, error) when exitCode <> 0 ->
                        return failed commandId (Failure.external exitCode) (Some exitCode) child output error
                    | Ok(exitCode, output, error) ->
                        let! verified =
                            match command with
                            | Solution(target, operation, operands, false) ->
                                Verify.verifySolution target operation operands cancellationToken
                            | Package(Some((PackageAdd | PackageRemove | PackageUpdate) as operation),
                                      project,
                                      file,
                                      version,
                                      operands,
                                      false) ->
                                let target =
                                    project
                                    |> Option.map Ok
                                    |> Option.defaultWith (fun () -> Paths.defaultProject ())

                                match file, target with
                                | Some path, _ ->
                                    let effective =
                                        match operation, version, operands with
                                        | PackageAdd, Some requested, [ package ] when not (package.Contains "@") ->
                                            [ $"{package}@{requested}" ]
                                        | _ -> operands

                                    match FileBasedPackageDirectives.Parse(File.ReadAllText path) with
                                    | Error failure -> Task.FromResult(Error failure)
                                    | Ok directives ->
                                        let present subject =
                                            let id, requested = Verify.packageSubject subject in
                                            FileBasedPackageDirectives.Contains(id, requested, directives)

                                        let correct =
                                            match operation with
                                            | PackageAdd -> effective.Length = 1 && present effective.Head
                                            | PackageRemove -> effective |> List.forall (present >> not)
                                            | PackageUpdate -> false
                                            | _ -> true

                                        if correct then
                                            Task.FromResult(Ok None)
                                        elif operation = PackageUpdate then
                                            Task.FromResult(
                                                Error(Failure.invalid "File-based package update is not supported.")
                                            )
                                        else
                                            Task.FromResult(
                                                Error(
                                                    Failure.verification
                                                        "The file-based app does not contain the requested package state."
                                                )
                                            )
                                | None, target ->
                                    match target with
                                    | Ok target ->
                                        let effectiveOperands =
                                            match operation, version, operands with
                                            | PackageAdd, Some requested, [ package ] when
                                                not (package.Contains("@", StringComparison.Ordinal))
                                                ->
                                                [ $"{package}@{requested}" ]
                                            | PackageAdd, _, _ :: _ :: _ -> []
                                            | _ -> operands

                                        if List.isEmpty effectiveOperands then
                                            Task.FromResult(
                                                Error(Failure.invalid "Package add accepts exactly one package ID.")
                                            )
                                        elif
                                            Path.GetExtension(target).Equals(".sln", StringComparison.OrdinalIgnoreCase)
                                            || Path
                                                .GetExtension(target)
                                                .Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                                        then
                                            Task.FromResult(
                                                Error(
                                                    Failure.invalid "Solution-wide package mutation is not supported."
                                                )
                                            )
                                        else
                                            Task.FromResult(Verify.verifyPackage operation target effectiveOperands)
                                    | Error message -> Task.FromResult(Error(Failure.invalid message))
                            | Reference(Some((ReferenceAdd | ReferenceRemove) as operation), project, operands, false) ->
                                let target =
                                    project
                                    |> Option.map Ok
                                    |> Option.defaultWith (fun () -> Paths.defaultProject ())

                                match target with
                                | Ok target -> Task.FromResult(Verify.verifyReferences operation target operands)
                                | Error message -> Task.FromResult(Error(Failure.invalid message))
                            | New(TemplateCreate, _, false, _, false) ->
                                Task.FromResult(Verify.verifyNew newOutput before)
                            | New(TemplateInstall, _, false, subjects, false) ->
                                if List.isEmpty subjects then
                                    Task.FromResult(Error(Failure.invalid "Template install requires a subject."))
                                else
                                    match TemplateEngineStateReader.Read(TemplateEngineStateReader.Root()) with
                                    | Ok state when
                                        subjects
                                        |> List.forall (fun subject ->
                                            TemplateEngineStateReader.Contains(subject, state))
                                        ->
                                        Task.FromResult(Ok None)
                                    | Ok _ ->
                                        Task.FromResult(
                                            Error(
                                                Failure.verification
                                                    "The requested template was not present after installation."
                                            )
                                        )
                                    | Error failure -> Task.FromResult(Error failure)
                            | New(TemplateUninstall, _, false, subjects, false) ->
                                if List.isEmpty subjects then
                                    Task.FromResult(Ok None)
                                else
                                    match TemplateEngineStateReader.Read(TemplateEngineStateReader.Root()) with
                                    | Ok state when
                                        subjects
                                        |> List.forall (fun subject ->
                                            not (TemplateEngineStateReader.Contains(subject, state)))
                                        ->
                                        Task.FromResult(Ok None)
                                    | Ok _ ->
                                        Task.FromResult(
                                            Error(
                                                Failure.verification "The requested template remained after uninstall."
                                            )
                                        )
                                    | Error failure -> Task.FromResult(Error failure)
                            | New(TemplateUpdate, _, false, _, false) ->
                                match TemplateEngineStateReader.Read(TemplateEngineStateReader.Root()) with
                                | Ok _ -> Task.FromResult(Ok None)
                                | Error failure -> Task.FromResult(Error failure)
                            | _ -> Task.FromResult(Ok None)

                        match verified with
                        | Ok revision -> return result commandId true revision [] (Some exitCode) child output error
                        | Error failure -> return failed commandId failure (Some exitCode) child output error
        }

    let execute arguments host mode cancellationToken =
        task {
            try
                return! executeCore arguments host mode cancellationToken
            with
            | :? XmlException
            | :? JsonException
            | :? ArgumentException
            | :? NotSupportedException
            | :? PathTooLongException ->
                return failed "" (Failure.invalid "The command target is invalid or malformed.") None [] "" ""
            | :? IOException
            | :? UnauthorizedAccessException ->
                return failed "" (Failure.internalFailure "The command target could not be read.") None [] "" ""
            | _ ->
                return
                    failed "" (Failure.internalFailure "The CLI broker encountered an internal failure.") None [] "" ""
        }

    let ExecuteAsync (arguments: string array, mode: BrokerMode, cancellationToken: CancellationToken) =
        execute arguments (productionHost ()) mode cancellationToken

    let InternalFailure () =
        failed "" (Failure.internalFailure "The CLI broker encountered an internal failure.") None [] "" ""

    let Render (result: BrokerResult) jsonMode (output: TextWriter) (error: TextWriter) =
        let diagnostic (value: WorkspaceDiagnostic) =
            {| severity = value.DiagnosticSeverity.ToString() |> ProcessExecution.sanitize
               code = value.DiagnosticCode.Value |> ProcessExecution.sanitize
               safeMessage = value.Message |> ProcessExecution.sanitize
               artifactPath =
                value.DiagnosticArtifactPath
                |> Option.map _.Value
                |> Option.map ProcessExecution.sanitize
               location =
                value.DiagnosticLocation
                |> Option.map (fun location ->
                    {| line = location.Line
                       column = location.Column |})
               retryable = value.Retryable
               correlationId = value.DiagnosticCorrelationId.Value.ToString() |> ProcessExecution.sanitize |}

        if jsonMode then
            let envelope =
                {| schemaVersion = 1
                   commandId = ProcessExecution.sanitize result.CommandId
                   success = result.Success
                   revision = result.Revision |> Option.map _.Value
                   result =
                    {| summary = result.Payload.Summary |> Option.map ProcessExecution.sanitize
                       childArguments = result.Payload.ChildArguments |> List.map ProcessExecution.sanitize
                       standardOutput = ProcessExecution.sanitize result.Payload.StandardOutput
                       standardError = ProcessExecution.sanitize result.Payload.StandardError |}
                   diagnostics = result.Diagnostics |> List.map diagnostic
                   externalExitCode = result.ExternalExitCode |}

            output.WriteLine(
                JsonSerializer.Serialize(
                    envelope,
                    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
                )
            )
        elif not result.Success then
            result.Diagnostics
            |> List.iter (fun value -> error.WriteLine($"{value.DiagnosticCode.Value}: {value.Message}"))

        if result.Success then
            0
        else
            result.ExternalExitCode |> Option.filter ((<>) 0) |> Option.defaultValue 1

module internal BrokerTestHooks =
    let ExecuteWithHostAsync
        (arguments: string array, fileName: string, prefix: string, cancellationToken: CancellationToken)
        =
        Broker.execute
            arguments
            { FileName = fileName
              Prefix = [ prefix ] }
            Json
            cancellationToken

    let ExecuteWithHostHumanAsync
        (
            arguments: string array,
            fileName: string,
            prefix: string,
            output: TextWriter,
            error: TextWriter,
            outputIsTty: bool,
            errorIsTty: bool,
            cancellationToken: CancellationToken
        ) =
        Broker.execute
            arguments
            { FileName = fileName
              Prefix = [ prefix ] }
            (Human(output, error, outputIsTty, errorIsTty))
            cancellationToken

    let PathEquals (caseSemantics: HostFileSystemCaseSemantics, left: string, right: string) =
        match caseSemantics with
        | HostFileSystemCaseSemantics.Insensitive -> StringComparer.OrdinalIgnoreCase.Equals(left, right)
        | _ -> StringComparer.Ordinal.Equals(left, right)
