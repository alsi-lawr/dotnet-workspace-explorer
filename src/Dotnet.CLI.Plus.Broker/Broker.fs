namespace Dotnet.CLI.Plus

#nowarn "3261"
#nowarn "3511"

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Xml
open System.Xml.Linq
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution

module internal Broker =
    type private PreparedCommand =
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
                let! opened = SolutionStore.OpenAsync(solutionPath, cancellationToken)

                return
                    match opened with
                    | Failure failure -> Error failure
                    | Success workspace when workspace.WorkspaceDescriptor.IsReadOnly ->
                        Error(BrokerFailure.unsupported ".slnf targets are read-only.")
                    | Success workspace ->
                        workspace.RootProjection.Projects
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
                let id, requested = Verify.packageSubject subject
                FileBasedPackageDirectives.Contains(id, requested, directives)

            if operands |> List.forall present then
                Ok None
            else
                Error(
                    BrokerFailure.verification
                        "The file-based app does not contain the requested package state."
                )

    let private verifyPackageUpdate target framework operands =
        match target with
        | ProjectTarget project ->
            if List.isEmpty operands then
                XDocument.Load project |> ignore
                Ok None
            else
                Verify.verifyPackage PackageUpdate project framework operands
        | FileTarget path -> verifyFilePackageUpdate path operands
        | SolutionTarget(_, projects) ->
            if List.isEmpty projects then
                Error(BrokerFailure.invalid "The solution does not contain any projects to verify.")
            elif List.isEmpty operands then
                projects |> List.iter (XDocument.Load >> ignore)
                Ok None
            else
                let presentInSolution subject =
                    projects
                    |> List.exists (fun project ->
                        match Verify.verifyPackage PackageUpdate project framework [ subject ] with
                        | Ok _ -> true
                        | Error _ -> false)

                if operands |> List.forall presentInSolution then
                    Ok None
                else
                    Error(
                        BrokerFailure.verification
                            "The refreshed solution does not contain the requested package state."
                    )

    let private verifyLegacyDirectory
        (solutionPath: string)
        (directoryPath: string)
        (cancellationToken: CancellationToken)
        =
        task {
            let solutionDirectory =
                Path.GetDirectoryName solutionPath
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let relativePath =
                let fullPath = Path.GetFullPath(directoryPath, solutionDirectory)

                Path.GetRelativePath(solutionDirectory, fullPath)
                |> fun path -> path.Replace('\\', '/').Trim '/'

            let expectedPath = $"/{relativePath}/"
            let! opened = SolutionStore.OpenAsync(solutionPath, cancellationToken)

            return
                match opened with
                | Failure failure -> Error failure
                | Success workspace when
                    workspace.RootProjection.Folders
                    |> Seq.exists (fun folder ->
                        String.Equals(folder.Path, expectedPath, StringComparison.Ordinal))
                    ->
                    Ok(Some workspace.WorkspaceDescriptor.WorkspaceRevision)
                | Success _ ->
                    Error(
                        BrokerFailure.verification
                            "The imported solution folder was not present after the command."
                    )
        }

    let private importLegacyDirectory
        (solutionPath: string)
        (directoryPath: string)
        (cancellationToken: CancellationToken)
        =
        task {
            let! opened = SolutionStore.OpenAsync(solutionPath, cancellationToken)

            match opened with
            | Failure failure -> return Error failure
            | Success workspace when workspace.WorkspaceDescriptor.IsReadOnly ->
                return
                    Error(
                        BrokerFailure.unsupported
                            ".slnf workspaces are read-only and cannot be mutated."
                    )
            | Success workspace ->
                let solutionDirectory =
                    Path.GetDirectoryName workspace.BackingPath.Value
                    |> Option.ofObj
                    |> Option.defaultValue (Directory.GetCurrentDirectory())

                let command =
                    { CommandId = CommandId.Create "solution.folder.import-directory"
                      TargetId = None
                      Arguments =
                        CommandArguments.Create
                            [ { ParameterId = CommandParameterId.Create "path"
                                Value =
                                  Path(
                                      WorkspaceArtifactPath.Create(
                                          Path.GetFullPath(directoryPath, solutionDirectory)
                                      )
                                  ) } ]
                      ExpectedRevision = workspace.WorkspaceDescriptor.WorkspaceRevision }

                let! planned =
                    SolutionPersistenceMutator.PlanAsync(workspace, command, cancellationToken)

                match planned with
                | Failure failure -> return Error failure
                | Success plan ->
                    let actions =
                        seq {
                            match plan.FileRename with
                            | Some rename ->
                                yield
                                    MutationAction.Rename(
                                        rename.Source.Value,
                                        rename.Destination.Value
                                    )
                            | None -> ()

                            yield MutationAction.ReplaceFile(plan.BackingPath.Value, plan.Contents)
                        }

                    let coordinator =
                        MutationCoordinator.CreateProduction(
                            WorkspaceArtifactPath.Create solutionDirectory,
                            fun () -> workspace.WorkspaceDescriptor.WorkspaceRevision
                        )

                    match coordinator.Prepare(plan.Request, actions) with
                    | Failure failure -> return Error failure
                    | Success preview ->
                        match
                            coordinator.Execute(
                                plan.Request,
                                actions,
                                preview.Confirmation,
                                cancellationToken
                            )
                        with
                        | Failure failure -> return Error failure
                        | Success Applied -> return Ok()
                        | Success(RolledBack failure) -> return Error failure
        }

    let private legacyDirectoryAdd raw cancellationToken =
        task {
            match raw with
            | command :: solutionPath :: "add" :: operation :: directoryPath :: [] when
                (command = "solution" || command = "sln")
                && (operation = "directory" || operation = "dir")
                ->
                let! imported = importLegacyDirectory solutionPath directoryPath cancellationToken

                match imported with
                | Error failure -> return Some(failed "solution" failure None [] "" "")
                | Ok() ->
                    let! refreshed =
                        verifyLegacyDirectory solutionPath directoryPath cancellationToken

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
                    DirectLaunchProfileCommands.execute
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
            let _, raw, parsed = Grammar.parse arguments

            let! profile = launchProfile parsed mode cancellationToken
            let! legacy = legacyDirectoryAdd raw cancellationToken

            match profile, legacy, parsed with
            | Some result, _, _ -> return result
            | None, Some result, _ -> return result
            | None, None, Error failure -> return failed "" failure None [] "" ""
            | None, None, Ok command ->
                let commandId = Grammar.commandId command

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
                                Verify.prepareSolution target operation operands cancellationToken

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
                                        BrokerFailure.invalid
                                            "Package add requires a package and one target."
                                    )
                            else
                                match
                                    file
                                    |> Option.orElse project
                                    |> Option.map Ok
                                    |> Option.defaultWith Paths.defaultProject
                                with
                                | Ok target when
                                    File.Exists target
                                    && (file.IsSome && Paths.isFileBasedApp target
                                        || file.IsNone && Paths.isProjectFile target)
                                    ->
                                    return Ok NoPreparedState
                                | Ok target when File.Exists target ->
                                    return
                                        Error(
                                            BrokerFailure.invalid
                                                "The package target type is not supported."
                                        )
                                | Ok _ ->
                                    return
                                        Error(
                                            BrokerFailure.invalid
                                                "The package target does not exist."
                                        )
                                | Error message -> return Error(BrokerFailure.invalid message)
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
                                        BrokerFailure.invalid
                                            "Package remove requires operands and one target."
                                    )
                            else
                                match
                                    file
                                    |> Option.orElse project
                                    |> Option.map Ok
                                    |> Option.defaultWith Paths.defaultProject
                                with
                                | Ok target when
                                    File.Exists target
                                    && (file.IsSome && Paths.isFileBasedApp target
                                        || file.IsNone && Paths.isProjectFile target)
                                    ->
                                    return Ok NoPreparedState
                                | Ok target when File.Exists target ->
                                    return
                                        Error(
                                            BrokerFailure.invalid
                                                "The package target type is not supported."
                                        )
                                | Ok _ ->
                                    return
                                        Error(
                                            BrokerFailure.invalid
                                                "The package target does not exist."
                                        )
                                | Error message -> return Error(BrokerFailure.invalid message)
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
                                        BrokerFailure.invalid
                                            "Reference mutation requires operands."
                                    )
                            else
                                match
                                    project
                                    |> Option.map Ok
                                    |> Option.defaultWith Paths.defaultProject
                                with
                                | Ok target when File.Exists target && Paths.isProjectFile target ->
                                    return Ok NoPreparedState
                                | Ok target when File.Exists target ->
                                    return
                                        Error(
                                            BrokerFailure.invalid
                                                "The reference target must be a project file."
                                        )
                                | Ok _ ->
                                    return
                                        Error(
                                            BrokerFailure.invalid
                                                "The reference target does not exist."
                                        )
                                | Error message -> return Error(BrokerFailure.invalid message)
                        | New(operation, _, false, subjects, false) when
                            operation = TemplateInstall && List.isEmpty subjects
                            ->
                            return
                                Error(BrokerFailure.invalid "Template install requires a subject.")
                        | New(operation, _, false, subjects, false) when
                            operation = TemplateCreate && List.isEmpty subjects
                            ->
                            return
                                Error(
                                    BrokerFailure.invalid "Template creation requires a template."
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

                            target, Verify.snapshot target
                        | _ -> "", Map.empty

                    let! executed = ProcessExecution.run host child mode cancellationToken

                    match executed with
                    | Error failure -> return failed commandId failure None child "" ""
                    | Ok(exitCode, output, error) when exitCode <> 0 ->
                        return
                            failed
                                commandId
                                (BrokerFailure.external exitCode)
                                (Some exitCode)
                                child
                                output
                                error
                    | Ok(exitCode, output, error) ->
                        let! verified =
                            match command with
                            | Solution(target, operation, operands, false) ->
                                Verify.verifySolution target operation operands cancellationToken
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
                                                BrokerFailure.internalFailure
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
                                        |> Option.defaultWith (fun () -> Paths.defaultProject ())

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
                                                let id, requested = Verify.packageSubject subject

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
                                                        BrokerFailure.verification (
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
                                                    BrokerFailure.invalid (
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
                                                    BrokerFailure.invalid (
                                                        "Solution-wide package mutation "
                                                        + "is not supported."
                                                    )
                                                )
                                            )
                                        else
                                            Task.FromResult(
                                                Verify.verifyPackage
                                                    operation
                                                    target
                                                    framework
                                                    effectiveOperands
                                            )
                                    | None, Error message ->
                                        Task.FromResult(Error(BrokerFailure.invalid message))
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
                                        |> Option.defaultWith (fun () -> Paths.defaultProject ())

                                    match target with
                                    | Ok target ->
                                        Task.FromResult(
                                            Verify.verifyReferences
                                                operation
                                                target
                                                framework
                                                operands
                                        )
                                    | Error message ->
                                        Task.FromResult(Error(BrokerFailure.invalid message))
                            | New(TemplateCreate, _, false, _, false) ->
                                Task.FromResult(Verify.verifyNew newOutput before)
                            | New(TemplateInstall, _, false, subjects, false) ->
                                if List.isEmpty subjects then
                                    Task.FromResult(
                                        Error(
                                            BrokerFailure.invalid
                                                "Template install requires a subject."
                                        )
                                    )
                                else
                                    match
                                        TemplateEngineStateReader.Read(
                                            TemplateEngineStateReader.Root()
                                        )
                                    with
                                    | Ok state when
                                        subjects
                                        |> List.forall (fun subject ->
                                            TemplateEngineStateReader.Contains(subject, state))
                                        ->
                                        Task.FromResult(Ok None)
                                    | Ok _ ->
                                        Task.FromResult(
                                            Error(
                                                BrokerFailure.verification (
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
                                        TemplateEngineStateReader.Read(
                                            TemplateEngineStateReader.Root()
                                        )
                                    with
                                    | Ok state when
                                        subjects
                                        |> List.forall (fun subject ->
                                            not (
                                                TemplateEngineStateReader.Contains(subject, state)
                                            ))
                                        ->
                                        Task.FromResult(Ok None)
                                    | Ok _ ->
                                        Task.FromResult(
                                            Error(
                                                BrokerFailure.verification (
                                                    "The requested template remained "
                                                    + "after uninstall."
                                                )
                                            )
                                        )
                                    | Error failure -> Task.FromResult(Error failure)
                            | New(TemplateUpdate, _, false, _, false) ->
                                match
                                    TemplateEngineStateReader.Read(TemplateEngineStateReader.Root())
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
                return failed "" (BrokerFailure.cancelled ()) None [] "" ""
            | :? XmlException
            | :? JsonException
            | :? ArgumentException
            | :? NotSupportedException
            | :? PathTooLongException ->
                return
                    failed
                        ""
                        (BrokerFailure.invalid "The command target is invalid or malformed.")
                        None
                        []
                        ""
                        ""
            | :? IOException
            | :? UnauthorizedAccessException ->
                return
                    failed
                        ""
                        (BrokerFailure.internalFailure "The command target could not be read.")
                        None
                        []
                        ""
                        ""
            | _ ->
                return
                    failed
                        ""
                        (BrokerFailure.internalFailure
                            "The CLI broker encountered an internal failure.")
                        None
                        []
                        ""
                        ""
        }

    let ExecuteAsync
        (arguments: string array, mode: BrokerMode, cancellationToken: CancellationToken)
        =
        execute arguments (productionHost ()) mode cancellationToken

    let InternalFailure () =
        failed
            ""
            (BrokerFailure.internalFailure "The CLI broker encountered an internal failure.")
            None
            []
            ""
            ""

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
               correlationId =
                value.DiagnosticCorrelationId.Value.ToString() |> ProcessExecution.sanitize |}

        if jsonMode then
            let envelope =
                {| schemaVersion = 1
                   commandId = ProcessExecution.sanitize result.CommandId
                   success = result.Success
                   revision = result.Revision |> Option.map _.Value
                   result =
                    {| summary = result.Payload.Summary |> Option.map ProcessExecution.sanitize
                       childArguments =
                        result.Payload.ChildArguments |> List.map ProcessExecution.sanitize
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
            |> List.iter (fun value ->
                let code = ProcessExecution.sanitize value.DiagnosticCode.Value
                let message = ProcessExecution.sanitize value.Message
                error.WriteLine $"{code}: {message}")

        if result.Success then
            0
        else
            result.ExternalExitCode |> Option.filter ((<>) 0) |> Option.defaultValue 1
