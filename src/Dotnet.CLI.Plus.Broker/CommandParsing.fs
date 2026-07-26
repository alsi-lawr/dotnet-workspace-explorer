namespace Dotnet.CLI.Plus

#nowarn "3261"
#nowarn "3511"

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

module internal Grammar =
    let private help tokens =
        tokens
        |> List.exists (fun token -> token = "--help" || token = "-h" || token = "-?")

    let private splitSentinel tokens =
        match tokens |> List.tryFindIndex ((=) "--") with
        | Some index -> tokens |> List.take index, tokens |> List.skip index
        | None -> tokens, []

    let private scan
        (values: Set<string>)
        (flags: Set<string>)
        (optionalBooleans: Set<string>)
        (tokens: string list)
        =
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

                if values |> Set.contains name || optionalBooleans |> Set.contains name then
                    collect tail (Map.add name value collected) positional unknown
                elif flags |> Set.contains name then
                    collect tail collected positional unknown
                else
                    collect tail collected positional (name :: unknown)
            | token :: value :: tail when
                optionalBooleans |> Set.contains token
                && (String.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                ->
                collect tail (Map.add token value collected) positional unknown
            | token :: value :: tail when values |> Set.contains token ->
                collect tail (Map.add token value collected) positional unknown
            | token :: tail when optionalBooleans |> Set.contains token ->
                collect tail (Map.add token "true" collected) positional unknown
            | token :: tail when flags |> Set.contains token ->
                collect tail collected positional unknown
            | token :: tail when token.StartsWith("-", StringComparison.Ordinal) ->
                collect tail collected positional (token :: unknown)
            | token :: tail -> collect tail collected (token :: positional) unknown

        collect tokens Map.empty [] []

    let private scanSolution =
        scan
            (Set.ofList [ "--solution-folder"; "-s" ])
            (Set.ofList [ "--in-root" ])
            (Set.ofList [ "--include-references" ])

    let private scanPackage =
        scan
            (Set.ofList
                [ "--project"
                  "--file"
                  "--version"
                  "-v"
                  "--framework"
                  "-f"
                  "--source"
                  "-s"
                  "--configfile"
                  "--package-directory"
                  "--verbosity" ])
            (Set.ofList [ "--prerelease"; "--vulnerable"; "--no-restore"; "-n"; "--interactive" ])
            Set.empty

    let private scanReference =
        scan
            (Set.ofList [ "--project"; "--framework"; "-f" ])
            (Set.ofList [ "--interactive"; "--no-restore" ])
            Set.empty

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
            (Set.ofList [ "--force"; "--no-update-check"; "--diagnostics"; "-d" ])
            (Set.ofList [ "--dry-run"; "--check-only" ])

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
            | [] -> Error(BrokerFailure.invalid "A plus command is required.")
            | command :: _ ->
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
                        Error(
                            BrokerFailure.invalid "Unknown solution option prevents verification."
                        )
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

                    let mutatingOperandsAmbiguous =
                        operation
                        |> Option.exists (fun value ->
                            value = PackageAdd || value = PackageRemove || value = PackageUpdate)
                        && operands.Length > 1

                    Ok(
                        Package(
                            operation,
                            options |> Map.tryFind "--project",
                            options |> Map.tryFind "--file",
                            options
                            |> Map.tryFind "--version"
                            |> Option.orElseWith (fun () -> options |> Map.tryFind "-v"),
                            options
                            |> Map.tryFind "--framework"
                            |> Option.orElseWith (fun () -> options |> Map.tryFind "-f"),
                            operands,
                            not (List.isEmpty unknown) || mutatingOperandsAmbiguous,
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

                    Ok(
                        Reference(
                            operation,
                            options |> Map.tryFind "--project",
                            options
                            |> Map.tryFind "--framework"
                            |> Option.orElseWith (fun () -> options |> Map.tryFind "-f"),
                            operands,
                            not (List.isEmpty unknown),
                            help beforeSentinel
                        )
                    )
                | "new" ->
                    let options, positions, _ = scanNew beforeSentinel.Tail

                    let optionEnabled name =
                        options
                        |> Map.tryFind name
                        |> Option.exists (fun value ->
                            not (String.Equals(value, "false", StringComparison.OrdinalIgnoreCase)))

                    let operation, operands =
                        match positions with
                        | "list" :: tail -> TemplateList, tail @ sentinelOperands
                        | "search" :: tail -> TemplateSearch, tail @ sentinelOperands
                        | "details" :: tail -> TemplateDetails, tail @ sentinelOperands
                        | "install" :: tail -> TemplateInstall, tail @ sentinelOperands
                        | "uninstall" :: tail -> TemplateUninstall, tail @ sentinelOperands
                        | "update" :: tail -> TemplateUpdate, tail @ sentinelOperands
                        | "create" :: tail -> TemplateCreate, tail @ sentinelOperands
                        | [] when Map.isEmpty options && List.isEmpty sentinelOperands ->
                            TemplateList, []
                        | tail -> TemplateCreate, tail @ sentinelOperands

                    Ok(
                        New(
                            operation,
                            options
                            |> Map.tryFind "--output"
                            |> Option.orElseWith (fun () -> options |> Map.tryFind "-o"),
                            optionEnabled "--dry-run" || optionEnabled "--check-only",
                            operands,
                            help beforeSentinel
                        )
                    )
                | "restore"
                | "build"
                | "test"
                | "run" -> Ok(Lifecycle(command, help beforeSentinel))
                | _ ->
                    Error(
                        BrokerFailure.invalid
                            "This dotnet command is not in the dotnet-plus compatibility grammar."
                    )

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
        | Package(Some(PackageAdd | PackageRemove | PackageUpdate), _, _, _, _, _, _, false) -> true
        | Reference(Some(ReferenceAdd | ReferenceRemove), _, _, _, _, false) -> true
        | New(TemplateCreate, _, false, _, false) -> true
        | _ -> false

module internal Paths =
    let isProjectFile (path: string) =
        match Path.GetExtension(path).ToLowerInvariant() with
        | ".csproj"
        | ".fsproj"
        | ".vbproj" -> true
        | _ -> false

    let isFileBasedApp (path: string) =
        String.Equals(Path.GetExtension path, ".cs", StringComparison.OrdinalIgnoreCase)

    let projects (directory: string) =
        Directory.EnumerateFiles(directory, "*.*proj", SearchOption.TopDirectoryOnly)
        |> Seq.filter isProjectFile
        |> Seq.sort
        |> Seq.toList

    let defaultProject () =
        match projects (Directory.GetCurrentDirectory()) with
        | [ project ] -> Ok project
        | [] -> Error "No project exists in the current directory."
        | _ -> Error "More than one project exists in the current directory; use --project."

    let expandSolutionOperand (operand: string) =
        if operand.IndexOfAny [| '*'; '?' |] >= 0 then
            let full = Path.GetFullPath operand
            let segments = full.Replace('\\', '/').Split '/'

            let wildcard =
                segments
                |> Array.findIndex (fun segment -> segment.IndexOfAny [| '*'; '?' |] >= 0)

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
            Error(BrokerFailure.invalid "Package source text is required.")
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
                            Error(
                                BrokerFailure.invalid
                                    "A file-based package directive is malformed."
                            )
                        else
                            Ok directives)
                (Ok [])
            |> Result.map List.rev

    let Contains (id: string, version: string option, directives: FileBasedPackageDirective list) =
        directives
        |> List.exists (fun directive ->
            String.Equals(directive.Id, id, StringComparison.OrdinalIgnoreCase)
            && version |> Option.forall (fun expected -> directive.Version = Some expected))

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
                    Directory.EnumerateFiles(
                        root,
                        "templatecache.json",
                        SearchOption.AllDirectories
                    )
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
        | :? JsonException -> Error(BrokerFailure.invalid "The template cache is malformed.")
        | :? IOException ->
            Error(BrokerFailure.internalFailure "The template cache could not be read.")

    let Contains (subject: string, state: TemplateEngineState) =
        let id = subject.Split("::", 2)[0]

        state.Packages
        |> List.exists (fun value ->
            let name = Path.GetFileNameWithoutExtension value in

            String.Equals(name, id, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase)
            || String.Equals(value, subject, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(id + "::", StringComparison.OrdinalIgnoreCase))

type internal PackageUpdateTarget =
    | ProjectTarget of string
    | FileTarget of string
    | SolutionTarget of string * string list

module internal PackageUpdateTargetResolver =
    let private differentPaths left right =
        not (String.Equals(left, right, StringComparison.Ordinal))

    let Resolve (project: string option, file: string option) =
        let selected =
            match project, file with
            | Some left, Some right when differentPaths left right ->
                Error(BrokerFailure.invalid "Package update target options conflict.")
            | Some path, _
            | _, Some path -> Ok path
            | None, None -> Ok(Directory.GetCurrentDirectory())

        match selected with
        | Error failure -> Error failure
        | Ok target when File.Exists target ->
            match Path.GetExtension(target).ToLowerInvariant() with
            | ".csproj"
            | ".fsproj"
            | ".vbproj" -> Ok(ProjectTarget target)
            | ".cs" -> Ok(FileTarget target)
            | ".sln"
            | ".slnx" -> Ok(SolutionTarget(target, []))
            | ".slnf" -> Error(BrokerFailure.unsupported ".slnf targets are read-only.")
            | _ -> Error(BrokerFailure.invalid "Package update target is unsupported.")
        | Ok target when Directory.Exists target ->
            let solutions =
                Directory.EnumerateFiles(target, "*.sln*", SearchOption.TopDirectoryOnly)
                |> Seq.toList

            let projects =
                Directory.EnumerateFiles(target, "*.*proj", SearchOption.TopDirectoryOnly)
                |> Seq.toList

            match solutions, projects with
            | [ solution ], _ -> Ok(SolutionTarget(solution, []))
            | [], [ project ] -> Ok(ProjectTarget project)
            | _ -> Error(BrokerFailure.invalid "Package update target is missing or ambiguous.")
        | Ok _ -> Error(BrokerFailure.invalid "Package update target does not exist.")
