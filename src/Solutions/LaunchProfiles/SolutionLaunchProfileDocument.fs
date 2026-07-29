namespace Dotnet.WorkspaceExplorer.Solutions

#nowarn "3261"

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Dotnet.WorkspaceExplorer.Workspaces

module internal SolutionLaunchProfileDocument =
    type private LaunchProfileProject =
        { Path: string
          Action: string
          Node: JsonObject }

    type private SolutionLaunchProfile =
        { Name: string
          Projects: LaunchProfileProject list
          Node: JsonObject }

    type private SolutionLaunchProfileDocument =
        { Path: string
          Root: JsonArray
          Profiles: SolutionLaunchProfile list }


    let private invalid message = Error message

    let private comparison path =
        match FileSystemCaseSensitivityDetector.DetectFromExistingPath path with
        | FileSystemCaseSensitivity.Insensitive -> StringComparison.OrdinalIgnoreCase
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
        Path.ChangeExtension(workspace.SolutionPath.Value, ".slnLaunch")

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
        Path.GetDirectoryName workspace.SolutionPath.Value
        |> Option.ofObj
        |> Option.defaultValue (Directory.GetCurrentDirectory())

    let private normalizeRelative root value =
        Path.GetRelativePath(root, value).Replace('\\', '/')

    let private normalizeProfilePath root (value: string) =
        value.Replace('\\', Path.DirectorySeparatorChar)
        |> fun path -> Path.GetFullPath(path, root)
        |> normalizeRelative root

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

    let renderSet workspace name paths =
        match read workspace with
        | Error error -> Error error
        | Ok document ->
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

            Ok(document.Path, render document)

    let renderRemove workspace name =
        match read workspace with
        | Error error -> Error error
        | Ok document when not (File.Exists document.Path) ->
            invalid $"Launch profile '{name}' does not exist."
        | Ok document ->
            match document.Profiles |> List.tryFind (fun profile -> profile.Name = name) with
            | None -> invalid $"Launch profile '{name}' does not exist."
            | Some profile ->
                document.Root.Remove profile.Node |> ignore
                Ok(document.Path, render document)


    let hasProfile workspace name projects =
        read workspace
        |> Result.map (fun document ->
            document.Profiles
            |> List.exists (fun profile ->
                profile.Name = name
                && profile.Projects |> List.map (fun project -> project.Path, project.Action) = projects))

    let lacksProfile workspace name =
        read workspace
        |> Result.map (fun document ->
            document.Profiles |> List.exists (fun profile -> profile.Name = name) |> not)
