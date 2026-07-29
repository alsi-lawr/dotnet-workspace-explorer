namespace Dotnet.WorkspaceExplorer.WorkspaceCommands

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.WorkspaceEditing

#nowarn "3261"

open System
open System.Collections.Immutable
open System.IO
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions

/// Maps node-oriented workspace command arguments to direct dotnet invocations.
/// The command-line project remains the subprocess authority.
module internal DotnetCommandCatalog =
    let private parameter id parameterType required displayName =
        CommandParameterDescriptor.Create(
            CommandParameterId.Create id,
            parameterType,
            required,
            displayName
        )

    let private projectCommand id displayName access parameters =
        CommandDescriptor.Create(
            CommandId.Create id,
            displayName,
            access,
            parameters,
            [ WorkspaceNodeKind.Project ]
        )

    let private templateCommand id displayName access parameters =
        CommandDescriptor.Create(
            CommandId.Create id,
            displayName,
            access,
            parameters,
            [ WorkspaceNodeKind.Workspace; WorkspaceNodeKind.SolutionFolder ]
        )

    let private extra =
        parameter "arguments" CommandParameterType.TextArray false "Additional dotnet arguments"

    let private framework =
        parameter "framework" CommandParameterType.Text false "Target framework"

    let private noRestore =
        parameter "noRestore" CommandParameterType.Boolean false "Do not restore"

    let private path =
        parameter "path" CommandParameterType.Path true "Referenced project"

    let private packageId = parameter "id" CommandParameterType.Text true "Package ID"

    let private version =
        parameter "version" CommandParameterType.Text false "Package version"

    let private template =
        parameter "template" CommandParameterType.Text true "Template short name"

    let private output =
        parameter "output" CommandParameterType.Path false "Output directory"

    let private dryRun =
        parameter "dryRun" CommandParameterType.Boolean false "Preview without creating files"

    let projectDescriptors =
        ImmutableArray.CreateRange
            [ projectCommand "reference.list" "List project references" CommandAccess.Read [ extra ]
              projectCommand
                  "reference.add"
                  "Add project reference"
                  CommandAccess.Write
                  [ path; framework; noRestore; extra ]
              projectCommand
                  "reference.remove"
                  "Remove project reference"
                  CommandAccess.Write
                  [ path; framework; noRestore; extra ]
              projectCommand
                  "package.list"
                  "List packages"
                  CommandAccess.Read
                  [ noRestore; framework; extra ]
              projectCommand
                  "package.add"
                  "Add package"
                  CommandAccess.Write
                  [ packageId; version; noRestore; framework; extra ]
              projectCommand
                  "package.remove"
                  "Remove package"
                  CommandAccess.Write
                  [ packageId; extra ]
              projectCommand
                  "package.update"
                  "Update package"
                  CommandAccess.Write
                  [ parameter "id" CommandParameterType.Text false "Package ID"; version; extra ] ]


    let templateDescriptors =
        ImmutableArray.CreateRange
            [ templateCommand "template.list" "List templates" CommandAccess.Read [ extra ]
              templateCommand
                  "template.describe"
                  "Show template details"
                  CommandAccess.Read
                  [ template; extra ]
              templateCommand
                  "template.create"
                  "Create template"
                  CommandAccess.Write
                  [ template; output; dryRun; extra ] ]


    let tryDescribe id =
        Seq.append projectDescriptors templateDescriptors
        |> Seq.tryFind (fun descriptor -> descriptor.Id = id)

    let discover (workspace: SolutionWorkspace) target =
        let candidates =
            match target with
            | Some target when
                workspace.Contents.Projects
                |> Seq.exists (fun project -> project.Node.Id = target)
                ->
                projectDescriptors
            | None -> templateDescriptors
            | Some target when
                workspace.Contents.Folders |> Seq.exists (fun folder -> folder.Node.Id = target)
                ->
                templateDescriptors
            | _ -> ImmutableArray<CommandDescriptor>.Empty

        if workspace.Descriptor.IsReadOnly then
            candidates
            |> Seq.filter (fun descriptor -> descriptor.Access = CommandAccess.Read)
            |> ImmutableArray.CreateRange
        else
            candidates


    let private argument id (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryPick (fun candidate ->
            if candidate.ParameterId.Value = id then
                Some candidate.Value
            else
                None)

    let private textArray arguments =
        match argument "arguments" arguments with
        | None -> Ok []
        | Some(TextArray values) -> Ok(values |> Seq.toList)
        | _ -> Error "Dotnet arguments must be a text array."

    let private optionalText id arguments =
        match argument id arguments with
        | None -> Ok None
        | Some(Text value) when not (String.IsNullOrWhiteSpace value) -> Ok(Some value)
        | _ -> Error $"'{id}' must be non-empty text."

    let private optionalBoolean id arguments =
        match argument id arguments with
        | None -> Ok false
        | Some(Boolean value) -> Ok value
        | _ -> Error $"'{id}' must be a boolean."

    let private requiredText id arguments =
        match argument id arguments with
        | Some(Text value) when not (String.IsNullOrWhiteSpace value) -> Ok value
        | _ -> Error $"'{id}' is required."

    let private optionalPath id arguments =
        match argument id arguments with
        | None -> Ok None
        | Some(Path value) -> Ok(Some value.Value)
        | _ -> Error $"'{id}' must be a path."

    let private requiredPath id arguments =
        match optionalPath id arguments with
        | Ok(Some value) -> Ok value
        | Ok None -> Error $"'{id}' is required."
        | Error error -> Error error

    let private projectPath (workspace: SolutionWorkspace) target =
        match
            target
            |> Option.bind (fun id ->
                workspace.Contents.Projects
                |> Seq.tryFind (fun project -> project.Node.Id = id)
                |> Option.map (fun project -> project.Path.AbsolutePath.Value))
        with
        | Some path -> Ok path
        | None -> Error "A project target is required."

    let targetProjectPath workspace request =
        projectPath workspace request.TargetWorkspaceNodeId

    let private templateDirectory (workspace: SolutionWorkspace) target =
        let root =
            Path.GetDirectoryName workspace.SolutionPath.Value
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        match target with
        | None -> Ok root
        | Some id ->
            match workspace.Contents.Folders |> Seq.tryFind (fun folder -> folder.Node.Id = id) with
            | Some _ ->
                // A solution folder is logical.  It selects later solution membership,
                // never an on-disk output directory.
                Ok root
            | None -> Error "The template target must be the workspace root or a solution folder."

    let templateOutput workspace request =
        match
            templateDirectory workspace request.TargetWorkspaceNodeId,
            optionalPath "output" request.Arguments
        with
        | Ok root, Ok output -> Ok(output |> Option.defaultValue root)
        | Error error, _
        | _, Error error -> Error error

    let isTemplateDryRun request =
        let passThroughDryRun =
            textArray request.Arguments
            |> Result.defaultValue []
            |> List.exists (fun value ->
                value = "--dry-run"
                || value = "--dry-run=true"
                || value = "--check-only"
                || value = "--check-only=true")

        request.CommandId.Value = "template.create"
        && (optionalBoolean "dryRun" request.Arguments |> Result.defaultValue false
            || passThroughDryRun)

    let private operationName commandId =
        match commandId with
        | "reference.list" -> Ok("reference", "list")
        | "reference.add" -> Ok("reference", "add")
        | "reference.remove" -> Ok("reference", "remove")
        | "package.list" -> Ok("package", "list")
        | "package.add" -> Ok("package", "add")
        | "package.remove" -> Ok("package", "remove")
        | "package.update" -> Ok("package", "update")
        | "template.list" -> Ok("new", "list")
        | "template.describe" -> Ok("new", "details")
        | "template.create" -> Ok("new", "create")
        | _ -> Error "The dotnet command is not supported."

    let argv (workspace: SolutionWorkspace) (request: CommandMutationRequest) =
        match operationName request.CommandId.Value, textArray request.Arguments with
        | Error error, _
        | _, Error error -> Error error
        | Ok(command, verb), Ok extraArguments ->
            match command with
            | "reference" ->
                let frameworkValue =
                    if verb = "list" then
                        Ok None
                    else
                        optionalText "framework" request.Arguments

                let noRestoreValue =
                    if verb = "list" then
                        Ok false
                    else
                        optionalBoolean "noRestore" request.Arguments

                match
                    projectPath workspace request.TargetWorkspaceNodeId,
                    frameworkValue,
                    noRestoreValue
                with
                | Error error, _, _ -> Error error
                | _, Error error, _ -> Error error
                | _, _, Error error -> Error error
                | Ok project, Ok frameworkValue, Ok _ ->
                    match
                        if verb = "list" then
                            Ok []
                        else
                            requiredPath "path" request.Arguments |> Result.map List.singleton
                    with
                    | Error error -> Error error
                    | Ok reference ->
                        let options =
                            [ yield command
                              yield verb
                              yield "--project"
                              yield project
                              match frameworkValue with
                              | Some value ->
                                  yield "--framework"
                                  yield value
                              | None -> ()
                              yield! reference
                              yield! extraArguments ]

                        Ok options
            | "package" ->
                let id =
                    if verb = "list" || verb = "update" then
                        optionalText "id" request.Arguments
                    else
                        requiredText "id" request.Arguments |> Result.map Some

                let frameworkValue =
                    if verb = "remove" || verb = "update" then
                        Ok None
                    else
                        optionalText "framework" request.Arguments

                let noRestoreValue =
                    if verb = "remove" || verb = "update" then
                        Ok false
                    else
                        optionalBoolean "noRestore" request.Arguments

                match
                    projectPath workspace request.TargetWorkspaceNodeId,
                    id,
                    optionalText "version" request.Arguments,
                    frameworkValue,
                    noRestoreValue
                with
                | Error error, _, _, _, _
                | _, Error error, _, _, _
                | _, _, Error error, _, _
                | _, _, _, Error error, _
                | _, _, _, _, Error error -> Error error
                | Ok _, Ok id, Ok versionValue, Ok _, Ok _ when versionValue.IsSome && id.IsNone ->
                    Error "A package version requires a package ID."
                | Ok project, Ok id, Ok versionValue, Ok frameworkValue, Ok noRestoreValue ->
                    let options =
                        [ yield command
                          yield verb
                          yield "--project"
                          yield project
                          if noRestoreValue then
                              yield "--no-restore"
                          match frameworkValue with
                          | Some value ->
                              yield "--framework"
                              yield value
                          | None -> ()
                          match id, versionValue with
                          | Some value, Some version when verb = "update" ->
                              yield $"{value}@{version}"
                          | Some value, Some version ->
                              yield value
                              yield "--version"
                              yield version
                          | Some value, None -> yield value
                          | None, None -> ()
                          | None, Some _ -> ()
                          yield! extraArguments ]

                    Ok options
            | "new" ->
                match templateDirectory workspace request.TargetWorkspaceNodeId with
                | Error error -> Error error
                | Ok workspaceRoot ->
                    match
                        if verb = "list" then
                            Ok(None, None, false)
                        elif verb = "details" then
                            requiredText "template" request.Arguments
                            |> Result.map (fun value -> Some value, None, false)
                        else
                            match
                                requiredText "template" request.Arguments,
                                templateOutput workspace request |> Result.map Some,
                                optionalBoolean "dryRun" request.Arguments
                            with
                            | Ok value, Ok destination, Ok preview ->
                                Ok(Some value, destination, preview)
                            | Error error, _, _
                            | _, Error error, _
                            | _, _, Error error -> Error error
                    with
                    | Error error -> Error error
                    | Ok(templateValue, destination, preview) ->
                        Ok
                            [ yield "new"
                              if verb = "list" then
                                  yield "list"
                              if verb = "details" then
                                  yield "details"
                              match templateValue with
                              | Some value -> yield value
                              | None -> ()
                              yield! extraArguments
                              if verb = "create" then
                                  yield "--output"
                                  yield destination |> Option.defaultValue workspaceRoot

                                  if preview then
                                      yield "--dry-run" ]
            | _ -> Error "The dotnet command is not supported."

    let isMutation commandId =
        commandId = "reference.add"
        || commandId = "reference.remove"
        || commandId = "package.add"
        || commandId = "package.remove"
        || commandId = "package.update"
        || commandId = "template.create"

    let isPackageMutation commandId =
        commandId = "package.add"
        || commandId = "package.remove"
        || commandId = "package.update"

    let requiresRestore commandId arguments =
        match commandId with
        | "reference.add"
        | "reference.remove" ->
            match optionalBoolean "noRestore" arguments with
            | Ok value -> not value
            | Error _ -> false
        | _ -> false
