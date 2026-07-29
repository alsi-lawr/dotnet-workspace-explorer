namespace Dotnet.WorkspaceExplorer.CommandLine

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions

#nowarn "3261"
#nowarn "3511"

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Xml
open System.Xml.Linq

module internal DirectCommandRunner =
    type private PreparedDirectCommand =
        | NoPreparedState
        | PreparedPackageUpdate of PackageUpdateTarget

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

    let private hydratePackageUpdateTarget target cancellationToken =
        task {
            match target with
            | ProjectTarget _
            | FileTarget _ -> return Ok target
            | SolutionTarget(solutionPath, _) ->
                let! opened = SolutionWorkspaceReader.OpenAsync(solutionPath, cancellationToken)

                return
                    match opened with
                    | Failure failure -> Error failure
                    | Success workspace when workspace.Descriptor.IsReadOnly ->
                        Error(DirectCommandFailures.unsupported ".slnf targets are read-only.")
                    | Success workspace ->
                        workspace.Contents.Projects
                        |> Seq.filter (fun project -> not project.IsFilteredOut)
                        |> Seq.map (fun project -> project.Path.AbsolutePath.Value)
                        |> Seq.toList
                        |> fun projects -> Ok(SolutionTarget(solutionPath, projects))
        }

    let private verifyFilePackageUpdate path operands =
        match FileBasedPackageDirectives.Parse(File.ReadAllText path) with
        | Error failure -> Error failure
        | Ok _ when List.isEmpty operands -> Ok None
        | Ok directives ->
            let present subject =
                let id, requested = PackagePostconditions.packageSubject subject
                FileBasedPackageDirectives.Contains(id, requested, directives)

            if operands |> List.forall present then
                Ok None
            else
                Error(
                    DirectCommandFailures.verification
                        "The file-based app does not contain the requested package state."
                )

    let private verifyPackageUpdate target framework operands =
        match target with
        | ProjectTarget project ->
            if List.isEmpty operands then
                XDocument.Load project |> ignore
                Ok None
            else
                PackagePostconditions.verifyPackage PackageUpdate project framework operands
        | FileTarget path -> verifyFilePackageUpdate path operands
        | SolutionTarget(_, projects) ->
            if List.isEmpty projects then
                Error(
                    DirectCommandFailures.invalid
                        "The solution does not contain any projects to verify."
                )
            elif List.isEmpty operands then
                projects |> List.iter (XDocument.Load >> ignore)
                Ok None
            else
                let presentInSolution subject =
                    projects
                    |> List.exists (fun project ->
                        match
                            PackagePostconditions.verifyPackage
                                PackageUpdate
                                project
                                framework
                                [ subject ]
                        with
                        | Ok _ -> true
                        | Error _ -> false)

                if operands |> List.forall presentInSolution then
                    Ok None
                else
                    Error(
                        DirectCommandFailures.verification
                            "The refreshed solution does not contain the requested package state."
                    )


    let private legacyDirectoryAdd raw cancellationToken =
        task {
            match raw with
            | command :: solutionPath :: "add" :: operation :: directoryPath :: [] when
                (command = "solution" || command = "sln")
                && (operation = "directory" || operation = "dir")
                ->
                let! imported =
                    LegacyDirectoryImport.import solutionPath directoryPath cancellationToken

                match imported with
                | Error failure -> return Some(failed "solution" failure None [] "" "")
                | Ok() ->
                    let! refreshed =
                        LegacyDirectoryImport.verify solutionPath directoryPath cancellationToken

                    return
                        match refreshed with
                        | Ok revision -> Some(result "solution" true revision [] (Some 0) [] "" "")
                        | Error failure -> Some(failed "solution" failure None [] "" "")
            | _ -> return None
        }

    let private launchProfile parsed mode cancellationToken =
        task {
            match parsed with
            | Ok(LaunchProfile(target, operation, name, projects, false)) ->
                let! completed =
                    CommandLineSolutionLaunchProfiles.execute
                        target
                        operation
                        name
                        projects
                        cancellationToken

                match completed with
                | Error failure -> return Some(failed "solution.launch" failure None [] "" "")
                | Ok(output, revision) ->
                    match mode with
                    | Human(writer, _, _, _) -> writer.Write output
                    | Json -> ()

                    return Some(result "solution.launch" true revision [] (Some 0) [] output "")
            | _ -> return None
        }

    let private executeCore arguments host mode cancellationToken =
        task {
            let _, raw, parsed = DirectCommandParser.parse arguments

            let! profile = launchProfile parsed mode cancellationToken
            let! legacy = legacyDirectoryAdd raw cancellationToken

            match profile, legacy, parsed with
            | Some result, _, _ -> return result
            | None, Some result, _ -> return result
            | None, None, Error failure -> return failed "" failure None [] "" ""
            | None, None, Ok command ->
                let commandId = DirectCommandParser.commandId command

                let child =
                    match raw with
                    | "sln" :: tail -> "solution" :: tail
                    | _ -> raw

                let! prepared =
                    task {
                        match command with
                        | Solution(target,
                                   Some(operation as (Add | Remove | Migrate)),
                                   operands,
                                   false) ->
                            let! workspace =
                                SolutionPostconditions.prepareSolution
                                    target
                                    operation
                                    operands
                                    cancellationToken

                            return workspace |> Result.map (fun _ -> NoPreparedState)
                        | Package(Some PackageAdd,
                                  project,
                                  file,
                                  _,
                                  _,
                                  operands,
                                  verificationAmbiguous,
                                  false) ->
                            if
                                List.isEmpty operands && not verificationAmbiguous
                                || project.IsSome && file.IsSome
                            then
                                return
                                    Error(
                                        DirectCommandFailures.invalid
                                            "Package add requires a package and one target."
                                    )
                            else
                                match
                                    file
                                    |> Option.orElse project
                                    |> Option.map Ok
                                    |> Option.defaultWith CommandTargetDiscovery.defaultProject
                                with
                                | Ok target when
                                    File.Exists target
                                    && (file.IsSome && CommandTargetDiscovery.isFileBasedApp target
                                        || file.IsNone
                                           && CommandTargetDiscovery.isProjectFile target)
                                    ->
                                    return Ok NoPreparedState
                                | Ok target when File.Exists target ->
                                    return
                                        Error(
                                            DirectCommandFailures.invalid
                                                "The package target type is not supported."
                                        )
                                | Ok _ ->
                                    return
                                        Error(
                                            DirectCommandFailures.invalid
                                                "The package target does not exist."
                                        )
                                | Error message ->
                                    return Error(DirectCommandFailures.invalid message)
                        | Package(Some PackageRemove,
                                  project,
                                  file,
                                  _,
                                  _,
                                  operands,
                                  verificationAmbiguous,
                                  false) ->
                            if
                                List.isEmpty operands && not verificationAmbiguous
                                || project.IsSome && file.IsSome
                            then
                                return
                                    Error(
                                        DirectCommandFailures.invalid
                                            "Package remove requires operands and one target."
                                    )
                            else
                                match
                                    file
                                    |> Option.orElse project
                                    |> Option.map Ok
                                    |> Option.defaultWith CommandTargetDiscovery.defaultProject
                                with
                                | Ok target when
                                    File.Exists target
                                    && (file.IsSome && CommandTargetDiscovery.isFileBasedApp target
                                        || file.IsNone
                                           && CommandTargetDiscovery.isProjectFile target)
                                    ->
                                    return Ok NoPreparedState
                                | Ok target when File.Exists target ->
                                    return
                                        Error(
                                            DirectCommandFailures.invalid
                                                "The package target type is not supported."
                                        )
                                | Ok _ ->
                                    return
                                        Error(
                                            DirectCommandFailures.invalid
                                                "The package target does not exist."
                                        )
                                | Error message ->
                                    return Error(DirectCommandFailures.invalid message)
                        | Package(Some PackageUpdate, project, file, _, _, _, _, false) ->
                            match PackageUpdateTargetResolver.Resolve(project, file) with
                            | Error failure -> return Error failure
                            | Ok target ->
                                let! hydrated = hydratePackageUpdateTarget target cancellationToken
                                return hydrated |> Result.map PreparedPackageUpdate
                        | Reference(Some operation,
                                    project,
                                    _,
                                    operands,
                                    verificationAmbiguous,
                                    false) when
                            operation = ReferenceAdd || operation = ReferenceRemove
                            ->
                            if List.isEmpty operands && not verificationAmbiguous then
                                return
                                    Error(
                                        DirectCommandFailures.invalid
                                            "Reference mutation requires operands."
                                    )
                            else
                                match
                                    project
                                    |> Option.map Ok
                                    |> Option.defaultWith CommandTargetDiscovery.defaultProject
                                with
                                | Ok target when
                                    File.Exists target
                                    && CommandTargetDiscovery.isProjectFile target
                                    ->
                                    return Ok NoPreparedState
                                | Ok target when File.Exists target ->
                                    return
                                        Error(
                                            DirectCommandFailures.invalid
                                                "The reference target must be a project file."
                                        )
                                | Ok _ ->
                                    return
                                        Error(
                                            DirectCommandFailures.invalid
                                                "The reference target does not exist."
                                        )
                                | Error message ->
                                    return Error(DirectCommandFailures.invalid message)
                        | New(operation, _, false, subjects, false) when
                            operation = TemplateInstall && List.isEmpty subjects
                            ->
                            return
                                Error(
                                    DirectCommandFailures.invalid
                                        "Template install requires a subject."
                                )
                        | New(operation, _, false, subjects, false) when
                            operation = TemplateCreate && List.isEmpty subjects
                            ->
                            return
                                Error(
                                    DirectCommandFailures.invalid
                                        "Template creation requires a template."
                                )
                        | _ -> return Ok NoPreparedState
                    }

                match prepared with
                | Error failure -> return failed commandId failure None child "" ""
                | Ok preparedState ->
                    let newOutput, before =
                        match command with
                        | New(TemplateCreate, output, false, _, false) ->
                            let target =
                                output |> Option.defaultValue (Directory.GetCurrentDirectory()) in

                            target, TemplatePostconditions.snapshot target
                        | _ -> "", Map.empty

                    let! executed = DotnetProcess.run host child mode cancellationToken

                    match executed with
                    | Error failure -> return failed commandId failure None child "" ""
                    | Ok(exitCode, output, error) when exitCode <> 0 ->
                        return
                            failed
                                commandId
                                (DirectCommandFailures.external exitCode)
                                (Some exitCode)
                                child
                                output
                                error
                    | Ok(exitCode, output, error) ->
                        let! verified =
                            match command with
                            | Solution(target, operation, operands, false) ->
                                SolutionPostconditions.verifySolution
                                    target
                                    operation
                                    operands
                                    cancellationToken
                            | Package(Some PackageUpdate,
                                      _,
                                      _,
                                      _,
                                      framework,
                                      operands,
                                      verificationAmbiguous,
                                      false) ->
                                if verificationAmbiguous then
                                    Task.FromResult(Ok None)
                                else
                                    match preparedState with
                                    | PreparedPackageUpdate target ->
                                        Task.FromResult(
                                            verifyPackageUpdate target framework operands
                                        )
                                    | NoPreparedState ->
                                        Task.FromResult(
                                            Error(
                                                DirectCommandFailures.internalFailure
                                                    "Package update state was not prepared."
                                            )
                                        )
                            | Package(Some(PackageAdd | PackageRemove | PackageUpdate as operation),
                                      project,
                                      file,
                                      version,
                                      framework,
                                      operands,
                                      verificationAmbiguous,
                                      false) ->
                                if verificationAmbiguous then
                                    Task.FromResult(Ok None)
                                else
                                    let target =
                                        project
                                        |> Option.map Ok
                                        |> Option.defaultWith (fun () ->
                                            CommandTargetDiscovery.defaultProject ())

                                    match file, target with
                                    | Some path, _ ->
                                        let effective =
                                            match operation with
                                            | PackageAdd ->
                                                match version, operands with
                                                | Some requested, [ package ] when
                                                    not (package.Contains "@")
                                                    ->
                                                    [ $"{package}@{requested}" ]
                                                | _ -> operands
                                            | _ -> operands

                                        match
                                            FileBasedPackageDirectives.Parse(File.ReadAllText path)
                                        with
                                        | Error failure -> Task.FromResult(Error failure)
                                        | Ok directives ->
                                            let present subject =
                                                let id, requested =
                                                    PackagePostconditions.packageSubject subject

                                                FileBasedPackageDirectives.Contains(
                                                    id,
                                                    requested,
                                                    directives
                                                )

                                            let correct =
                                                match operation with
                                                | PackageAdd ->
                                                    effective.Length = 1 && present effective.Head
                                                | PackageRemove ->
                                                    effective |> List.forall (present >> not)
                                                | PackageUpdate -> effective |> List.forall present
                                                | _ -> true

                                            if correct then
                                                Task.FromResult(Ok None)
                                            else
                                                Task.FromResult(
                                                    Error(
                                                        DirectCommandFailures.verification (
                                                            "The file-based app does not contain "
                                                            + "the requested package state."
                                                        )
                                                    )
                                                )
                                    | None, Ok target ->
                                        let effectiveOperands =
                                            match operation, version, operands with
                                            | PackageAdd, Some requested, [ package ] when
                                                not (
                                                    package.Contains("@", StringComparison.Ordinal)
                                                )
                                                ->
                                                [ $"{package}@{requested}" ]
                                            | PackageAdd, _, _ :: _ :: _ -> []
                                            | _ -> operands

                                        if List.isEmpty effectiveOperands then
                                            Task.FromResult(
                                                Error(
                                                    DirectCommandFailures.invalid (
                                                        "Package add accepts exactly one "
                                                        + "package ID."
                                                    )
                                                )
                                            )
                                        elif
                                            Path
                                                .GetExtension(target)
                                                .Equals(".sln", StringComparison.OrdinalIgnoreCase)
                                            || Path
                                                .GetExtension(target)
                                                .Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                                        then
                                            Task.FromResult(
                                                Error(
                                                    DirectCommandFailures.invalid (
                                                        "Solution-wide package mutation "
                                                        + "is not supported."
                                                    )
                                                )
                                            )
                                        else
                                            Task.FromResult(
                                                PackagePostconditions.verifyPackage
                                                    operation
                                                    target
                                                    framework
                                                    effectiveOperands
                                            )
                                    | None, Error message ->
                                        Task.FromResult(
                                            Error(DirectCommandFailures.invalid message)
                                        )
                            | Reference(Some(ReferenceAdd | ReferenceRemove as operation),
                                        project,
                                        framework,
                                        operands,
                                        verificationAmbiguous,
                                        false) ->
                                if verificationAmbiguous then
                                    Task.FromResult(Ok None)
                                else
                                    let target =
                                        project
                                        |> Option.map Ok
                                        |> Option.defaultWith (fun () ->
                                            CommandTargetDiscovery.defaultProject ())

                                    match target with
                                    | Ok target ->
                                        Task.FromResult(
                                            ReferencePostconditions.verifyReferences
                                                operation
                                                target
                                                framework
                                                operands
                                        )
                                    | Error message ->
                                        Task.FromResult(
                                            Error(DirectCommandFailures.invalid message)
                                        )
                            | New(TemplateCreate, _, false, _, false) ->
                                Task.FromResult(TemplatePostconditions.verifyNew newOutput before)
                            | New(TemplateInstall, _, false, subjects, false) ->
                                if List.isEmpty subjects then
                                    Task.FromResult(
                                        Error(
                                            DirectCommandFailures.invalid
                                                "Template install requires a subject."
                                        )
                                    )
                                else
                                    match
                                        TemplateEngineInstallationReader.Read(
                                            TemplateEngineInstallationReader.Root()
                                        )
                                    with
                                    | Ok state when
                                        subjects
                                        |> List.forall (fun subject ->
                                            TemplateEngineInstallationReader.Contains(
                                                subject,
                                                state
                                            ))
                                        ->
                                        Task.FromResult(Ok None)
                                    | Ok _ ->
                                        Task.FromResult(
                                            Error(
                                                DirectCommandFailures.verification (
                                                    "The requested template was not present "
                                                    + "after installation."
                                                )
                                            )
                                        )
                                    | Error failure -> Task.FromResult(Error failure)
                            | New(TemplateUninstall, _, false, subjects, false) ->
                                if List.isEmpty subjects then
                                    Task.FromResult(Ok None)
                                else
                                    match
                                        TemplateEngineInstallationReader.Read(
                                            TemplateEngineInstallationReader.Root()
                                        )
                                    with
                                    | Ok state when
                                        subjects
                                        |> List.forall (fun subject ->
                                            not (
                                                TemplateEngineInstallationReader.Contains(
                                                    subject,
                                                    state
                                                )
                                            ))
                                        ->
                                        Task.FromResult(Ok None)
                                    | Ok _ ->
                                        Task.FromResult(
                                            Error(
                                                DirectCommandFailures.verification (
                                                    "The requested template remained "
                                                    + "after uninstall."
                                                )
                                            )
                                        )
                                    | Error failure -> Task.FromResult(Error failure)
                            | New(TemplateUpdate, _, false, _, false) ->
                                match
                                    TemplateEngineInstallationReader.Read(
                                        TemplateEngineInstallationReader.Root()
                                    )
                                with
                                | Ok _ -> Task.FromResult(Ok None)
                                | Error failure -> Task.FromResult(Error failure)
                            | _ -> Task.FromResult(Ok None)

                        match verified with
                        | Ok revision ->
                            return
                                result commandId true revision [] (Some exitCode) child output error
                        | Error failure ->
                            return failed commandId failure (Some exitCode) child output error
        }

    let execute arguments host mode cancellationToken =
        task {
            try
                return! executeCore arguments host mode cancellationToken
            with
            | :? OperationCanceledException ->
                return failed "" (DirectCommandFailures.cancelled ()) None [] "" ""
            | :? XmlException
            | :? JsonException
            | :? ArgumentException
            | :? NotSupportedException
            | :? PathTooLongException ->
                return
                    failed
                        ""
                        (DirectCommandFailures.invalid "The command target is invalid or malformed.")
                        None
                        []
                        ""
                        ""
            | :? IOException
            | :? UnauthorizedAccessException ->
                return
                    failed
                        ""
                        (DirectCommandFailures.internalFailure
                            "The command target could not be read.")
                        None
                        []
                        ""
                        ""
            | _ ->
                return
                    failed
                        ""
                        (DirectCommandFailures.internalFailure
                            "The Workspace Explorer command line encountered an internal failure.")
                        None
                        []
                        ""
                        ""
        }

    let ExecuteAsync
        (arguments: string array, mode: CommandOutputMode, cancellationToken: CancellationToken)
        =
        execute arguments (productionHost ()) mode cancellationToken

    let InternalFailure () =
        failed
            ""
            (DirectCommandFailures.internalFailure
                "The Workspace Explorer command line encountered an internal failure.")
            None
            []
            ""
            ""
