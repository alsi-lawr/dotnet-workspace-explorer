namespace Dotnet.WorkspaceExplorer.Solutions

open Dotnet.WorkspaceExplorer.Workspaces

#nowarn "3261"

open System
open System.IO

module internal SolutionLaunchProfiles =
    type private ExpectedLaunchProfileState =
        | HasProfile of string * (string * string) list
        | LacksProfile of string

    let private invalid message = Error message

    let private comparison path =
        match FileSystemCaseSensitivityDetector.DetectFromExistingPath path with
        | FileSystemCaseSensitivity.Insensitive -> StringComparison.OrdinalIgnoreCase
        | _ -> StringComparison.Ordinal

    let private solutionDirectory (workspace: SolutionWorkspace) =
        Path.GetDirectoryName workspace.SolutionPath.Value
        |> Option.ofObj
        |> Option.defaultValue (Directory.GetCurrentDirectory())

    let private normalizeRelative root value =
        Path.GetRelativePath(root, value).Replace('\\', '/')

    let private resolveProject (workspace: SolutionWorkspace) operand =
        let root = solutionDirectory workspace
        let candidate = Path.GetFullPath(operand, root)

        workspace.Contents.Projects
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


    let path workspace =
        SolutionLaunchProfileDocument.path workspace

    let names workspace =
        SolutionLaunchProfileDocument.names workspace

    let private prepare (workspace: SolutionWorkspace) name operands =
        if workspace.Descriptor.IsReadOnly then
            invalid ".slnf workspaces are read-only."
        elif String.IsNullOrWhiteSpace name then
            invalid "Launch profile name is required."
        elif List.isEmpty operands then
            SolutionLaunchProfileDocument.renderRemove workspace name
            |> Result.map (fun (path, contents) -> path, contents, LacksProfile name)
        else
            resolveProjects workspace operands
            |> Result.bind (fun paths ->
                SolutionLaunchProfileDocument.renderSet workspace name paths
                |> Result.map (fun (path, contents) ->
                    path,
                    contents,
                    HasProfile(
                        name,
                        paths |> List.map (fun path -> path, "StartWithoutDebugging")
                    )))

    let private verify (workspace: SolutionWorkspace) (expected: ExpectedLaunchProfileState) =
        match expected with
        | HasProfile(name, projects) ->
            SolutionLaunchProfileDocument.hasProfile workspace name projects
            |> Result.bind (fun matches ->
                if matches then
                    Ok()
                else
                    invalid "Launch profile write could not be verified.")
        | LacksProfile name ->
            SolutionLaunchProfileDocument.lacksProfile workspace name
            |> Result.bind (fun matches ->
                if matches then
                    Ok()
                else
                    invalid "Launch profile removal could not be verified.")


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
