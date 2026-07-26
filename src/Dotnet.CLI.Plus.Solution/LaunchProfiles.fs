namespace Dotnet.CLI.Plus.Solution

#nowarn "3261"

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Dotnet.CLI.Plus.Core

module internal LaunchProfiles =
    type private ProfileProject =
        { Path: string
          Action: string
          Node: JsonObject }

    type private Profile =
        { Name: string
          Projects: ProfileProject list
          Node: JsonObject }

    type private Document =
        { Path: string
          Root: JsonArray
          Profiles: Profile list }

    type private ExpectedState =
        | HasProfile of name: string * projects: (string * string) list
        | LacksProfile of name: string

    let private invalid message = Error message

    let private comparison path =
        match HostFileSystemCaseDetector.DetectFromExistingPath path with
        | HostFileSystemCaseSemantics.Insensitive -> StringComparison.OrdinalIgnoreCase
        | _ -> StringComparison.Ordinal

    let private requiredString name (node: JsonNode option) =
        match node with
        | Some(:? JsonValue as value) ->
            match value.TryGetValue<string>() with
            | true, text when not (String.IsNullOrWhiteSpace text) -> Ok text
            | _ -> invalid $"Launch profile '{name}' must be non-empty text."
        | _ -> invalid $"Launch profile '{name}' must be non-empty text."

    let private requiredArray name (node: JsonNode option) =
        match node with
        | Some(:? JsonArray as value) -> Ok value
        | _ -> invalid $"Launch profile '{name}' must be an array."

    let private parseProject (node: JsonNode) =
        match node with
        | :? JsonObject as value ->
            match
                requiredString "Path" (value["Path"] |> Option.ofObj),
                requiredString "Action" (value["Action"] |> Option.ofObj)
            with
            | Ok path, Ok action ->
                Ok
                    { Path = path
                      Action = action
                      Node = value }
            | Error error, _
            | _, Error error -> Error error
        | _ -> invalid "Launch profile projects must be objects."

    let private parseProfile (node: JsonNode) =
        match node with
        | :? JsonObject as value ->
            match
                requiredString "Name" (value["Name"] |> Option.ofObj),
                requiredArray "Projects" (value["Projects"] |> Option.ofObj)
            with
            | Ok name, Ok projects ->
                projects
                |> Seq.map parseProject
                |> Seq.fold
                    (fun state item ->
                        match state, item with
                        | Ok values, Ok project -> Ok(project :: values)
                        | Error error, _
                        | _, Error error -> Error error)
                    (Ok [])
                |> Result.map (fun parsed ->
                    { Name = name
                      Projects = List.rev parsed
                      Node = value })
            | Error error, _
            | _, Error error -> Error error
        | _ -> invalid "Launch profiles must be objects."

    let private parse path (root: JsonArray) =
        root
        |> Seq.map parseProfile
        |> Seq.fold
            (fun state item ->
                match state, item with
                | Ok values, Ok profile -> Ok(profile :: values)
                | Error error, _
                | _, Error error -> Error error)
            (Ok [])
        |> Result.bind (fun parsed ->
            let profiles = List.rev parsed

            if profiles |> Seq.countBy _.Name |> Seq.exists (fun (_, count) -> count > 1) then
                invalid "Launch profile names must be unique."
            else
                Ok
                    { Path = path
                      Root = root
                      Profiles = profiles })

    let path (workspace: SolutionWorkspace) =
        Path.ChangeExtension(workspace.BackingPath.Value, ".slnLaunch")

    let private read (workspace: SolutionWorkspace) =
        let file = path workspace

        if not (File.Exists file) then
            Ok
                { Path = file
                  Root = JsonArray()
                  Profiles = [] }
        else
            try
                use stream = File.OpenRead file

                match JsonNode.Parse stream with
                | :? JsonArray as root -> parse file root
                | _ -> invalid "Launch profile root must be an array."
            with
            | :? JsonException -> invalid "Launch profile file is malformed."
            | :? IOException
            | :? UnauthorizedAccessException -> Error "Launch profile file could not be read."

    let names workspace =
        read workspace
        |> Result.map (fun document -> document.Profiles |> List.map _.Name)

    let private solutionDirectory (workspace: SolutionWorkspace) =
        Path.GetDirectoryName workspace.BackingPath.Value
        |> Option.ofObj
        |> Option.defaultValue (Directory.GetCurrentDirectory())

    let private normalizeRelative root value =
        Path.GetRelativePath(root, value).Replace('\\', '/')

    let private normalizeProfilePath root (value: string) =
        value.Replace('\\', Path.DirectorySeparatorChar)
        |> fun path -> Path.GetFullPath(path, root)
        |> normalizeRelative root

    let private resolveProject (workspace: SolutionWorkspace) operand =
        let root = solutionDirectory workspace
        let candidate = Path.GetFullPath(operand, root)

        workspace.RootProjection.Projects
        |> Seq.filter (fun project ->
            String.Equals(project.Path.AbsolutePath.Value, candidate, comparison root))
        |> Seq.toList
        |> function
            | [ project ] -> Ok(normalizeRelative root project.Path.AbsolutePath.Value)
            | [] -> invalid $"'{operand}' is not a project in the opened solution."
            | _ -> invalid $"'{operand}' identifies multiple projects."

    let private resolveProjects workspace operands =
        let rec collect resolved remaining =
            match remaining with
            | [] -> Ok(List.rev resolved)
            | operand :: tail ->
                match resolveProject workspace operand with
                | Ok path -> collect (path :: resolved) tail
                | Error error -> Error error

        collect [] operands

    let private retainedProject root pathComparison selected profile =
        profile.Projects
        |> List.tryFind (fun project ->
            String.Equals(normalizeProfilePath root project.Path, selected, pathComparison))
        |> Option.map (fun project -> project.Node.DeepClone() :?> JsonObject)
        |> Option.defaultWith (fun () -> JsonObject())

    let private render document =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
        document.Root.WriteTo writer
        writer.Flush()
        stream.ToArray()

    let private renderSet workspace name operands =
        match read workspace, resolveProjects workspace operands with
        | Error error, _
        | _, Error error -> Error error
        | Ok document, Ok paths ->
            let profile = document.Profiles |> List.tryFind (fun item -> item.Name = name)

            let target =
                profile |> Option.map _.Node |> Option.defaultWith (fun () -> JsonObject())

            let root = solutionDirectory workspace
            let pathComparison = comparison root
            let projects = JsonArray()

            for selected in paths do
                let project =
                    profile
                    |> Option.map (retainedProject root pathComparison selected)
                    |> Option.defaultWith (fun () -> JsonObject())

                project["Path"] <- JsonValue.Create selected
                project["Action"] <- JsonValue.Create "StartWithoutDebugging"
                projects.Add project

            target["Name"] <- JsonValue.Create name
            target["Projects"] <- projects

            if profile.IsNone then
                document.Root.Add target

            Ok(
                document.Path,
                render document,
                HasProfile(name, paths |> List.map (fun path -> path, "StartWithoutDebugging"))
            )

    let private renderRemove workspace name =
        match read workspace with
        | Error error -> Error error
        | Ok document when not (File.Exists document.Path) ->
            invalid $"Launch profile '{name}' does not exist."
        | Ok document ->
            match document.Profiles |> List.tryFind (fun profile -> profile.Name = name) with
            | None -> invalid $"Launch profile '{name}' does not exist."
            | Some profile ->
                document.Root.Remove profile.Node |> ignore
                Ok(document.Path, render document, LacksProfile name)

    let private prepare (workspace: SolutionWorkspace) name operands =
        if workspace.WorkspaceDescriptor.IsReadOnly then
            invalid ".slnf workspaces are read-only."
        elif String.IsNullOrWhiteSpace name then
            invalid "Launch profile name is required."
        elif List.isEmpty operands then
            renderRemove workspace name
        else
            renderSet workspace name operands

    let private verify (workspace: SolutionWorkspace) (expected: ExpectedState) =
        read workspace
        |> Result.bind (fun document ->
            match expected with
            | HasProfile(name, projects) ->
                match document.Profiles |> List.tryFind (fun profile -> profile.Name = name) with
                | Some profile when
                    (profile.Projects |> List.map (fun project -> project.Path, project.Action)) = projects
                    ->
                    Ok()
                | _ -> invalid "Launch profile write could not be verified."
            | LacksProfile name when
                document.Profiles |> List.exists (fun profile -> profile.Name = name)
                ->
                invalid "Launch profile removal could not be verified."
            | LacksProfile _ -> Ok())

    let private write (path: string) (contents: byte array) =
        let directory =
            Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue "."

        let stage =
            Path.Combine(directory, $".{Path.GetFileName path}.{Guid.NewGuid():N}.tmp")

        try
            try
                File.WriteAllBytes(stage, contents)
                File.Move(stage, path, true)
                Ok()
            with
            | :? IOException
            | :? UnauthorizedAccessException -> Error "Launch profile file could not be written."
        finally
            if File.Exists stage then
                File.Delete stage

    let private prepareFor workspace name projects = prepare workspace name projects

    let prepareSet workspace name projects =
        if List.isEmpty projects then
            invalid "Launch profile projects are required."
        else
            prepareFor workspace name projects
            |> Result.map (fun (path, contents, _) -> path, contents)

    let prepareRemove workspace name =
        prepareFor workspace name []
        |> Result.map (fun (path, contents, _) -> path, contents)

    let verifySet workspace name projects =
        if List.isEmpty projects then
            invalid "Launch profile projects are required."
        else
            resolveProjects workspace projects
            |> Result.bind (fun paths ->
                verify
                    workspace
                    (HasProfile(
                        name,
                        paths |> List.map (fun path -> path, "StartWithoutDebugging")
                    )))

    let verifyRemove workspace name =
        if String.IsNullOrWhiteSpace name then
            invalid "Launch profile name is required."
        else
            verify workspace (LacksProfile name)

    let set workspace name projects =
        if List.isEmpty projects then
            invalid "Launch profile projects are required."
        else
            prepareFor workspace name projects
            |> Result.bind (fun (path, contents, expected) ->
                write path contents |> Result.bind (fun () -> verify workspace expected))

    let remove workspace name =
        prepareFor workspace name []
        |> Result.bind (fun (path, contents, expected) ->
            write path contents |> Result.bind (fun () -> verify workspace expected))
