namespace Dotnet.CLI.Plus

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution

/// The supported direct grammar. This is deliberately a closed table: this tool is
/// a compatibility broker for these SDK commands, never a proxy for arbitrary dotnet commands.
type CommandCompatibility =
    { PlusGrammar: string
      ChildArguments: string
      PassThroughOptions: string
      UnsupportedCases: string }

[<RequireQualifiedAccess>]
module CompatibilityTable =
    /// Based on the locally installed .NET SDK 10.0.301 help.
    let Commands =
        [ { PlusGrammar = "solution|sln [<SLN_FILE>] add|list|remove|migrate [options]"
            ChildArguments = "solution ... (sln is normalized to solution)"
            PassThroughOptions = "All SDK arguments after the plus command are preserved verbatim."
            UnsupportedCases = "Mutating a .slnf; arbitrary dotnet commands." }
          { PlusGrammar = "package search|add|list|remove|update|download [options]"
            ChildArguments = "package ..."
            PassThroughOptions = "All SDK arguments after the plus command are preserved verbatim."
            UnsupportedCases = "Mutations without --project, and arbitrary dotnet commands." }
          { PlusGrammar = "reference add|list|remove [options]"
            ChildArguments = "reference ..."
            PassThroughOptions = "All SDK arguments after the plus command are preserved verbatim."
            UnsupportedCases = "Mutations without --project, and arbitrary dotnet commands." }
          { PlusGrammar = "new [template|create|install|uninstall|update|search|list|details] [options]"
            ChildArguments = "new ..."
            PassThroughOptions = "All SDK arguments after the plus command are preserved verbatim."
            UnsupportedCases = "Mutating templates without --output, and arbitrary dotnet commands." }
          { PlusGrammar = "restore|build|test|run [options]"
            ChildArguments = "same command and arguments"
            PassThroughOptions = "All SDK arguments after the plus command are preserved verbatim."
            UnsupportedCases = "Lifecycle policy and orchestration (owned by T-011)." } ]

type BrokerDiagnostic =
    { Code: string
      Message: string
      Retryable: bool }

type BrokerResult =
    { CommandId: string
      Success: bool
      Revision: int64 option
      Result: BrokerPayload
      Diagnostics: BrokerDiagnostic list
      ExternalExitCode: int option
      StandardOutput: string
      StandardError: string }

and BrokerPayload =
    { Summary: string option
      ChildArguments: string list
      StandardOutput: string
      StandardError: string }

module private BrokerFailure =
    let diagnostic code message retryable =
        WorkspaceDiagnostic.CreateSimple(
            WorkspaceDiagnosticSeverity.Error,
            WorkspaceDiagnosticCode.Create code,
            message,
            retryable,
            CorrelationId.New()
        )

    let toBrokerDiagnostic (failure: WorkspaceFailure) =
        let diagnostic = failure.Diagnostic

        { Code = failure.Code.Value
          Message = diagnostic.Message
          Retryable = diagnostic.Retryable }

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

module private ArgumentGrammar =
    let private supported =
        Set.ofList
            [ "solution"
              "sln"
              "package"
              "reference"
              "new"
              "restore"
              "build"
              "test"
              "run" ]

    let private mutatingSolutionCommands = Set.ofList [ "add"; "remove"; "migrate" ]
    let private mutatingPackageCommands = Set.ofList [ "add"; "remove"; "update" ]
    let private mutatingReferenceCommands = Set.ofList [ "add"; "remove" ]

    /// --json is owned by dotnet-plus only when it is the leading argument.
    /// A literal --json after -- remains a child argument.
    let parse (arguments: string array) =
        match arguments |> Array.toList with
        | "--json" :: remaining -> true, remaining
        | remaining -> false, remaining

    let childArguments command arguments =
        (if command = "sln" then "solution" else command) :: arguments

    let commandId command =
        if command = "sln" then "solution" else command

    let isMutating command (arguments: string list) =
        match command, arguments with
        | ("solution" | "sln"), first :: second :: _ when mutatingSolutionCommands.Contains second -> true
        | ("solution" | "sln"), first :: _ when mutatingSolutionCommands.Contains first -> true
        | "package", first :: _ -> mutatingPackageCommands.Contains first
        | "reference", first :: _ -> mutatingReferenceCommands.Contains first
        | "new", "list" :: _
        | "new", "search" :: _
        | "new", "details" :: _ -> false
        | "new", _ -> not (arguments |> List.contains "--dry-run")
        | _ -> false

    let containsSolutionFilter (arguments: string list) =
        arguments
        |> List.exists (fun argument -> argument.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))

    let legacyDirectoryAdd command arguments =
        match command, arguments with
        | ("solution" | "sln"), target :: "add" :: ("directory" | "dir") :: directory :: [] -> Some(target, directory)
        | _ -> None

    let tryProject arguments =
        arguments
        |> List.pairwise
        |> List.tryPick (function
            | "--project", project -> Some project
            | _ -> None)

    let tryOutput arguments =
        arguments
        |> List.pairwise
        |> List.tryPick (function
            | ("--output" | "-o"), output -> Some output
            | _ -> None)

    let isSupported command = supported.Contains command

    let solutionInvocation (arguments: string list) =
        let commands = Set.ofList [ "add"; "list"; "remove"; "migrate" ]

        match arguments with
        | option :: _ when option.StartsWith("-", StringComparison.Ordinal) -> None, None, []
        | command :: remaining when commands.Contains command -> None, Some command, remaining
        | target :: command :: remaining when commands.Contains command -> Some target, Some command, remaining
        | target :: _ -> Some target, None, []
        | [] -> None, None, []

    let requiresConcreteVerification command arguments =
        match command with
        | "package"
        | "reference" -> isMutating command arguments && Option.isNone (tryProject arguments)
        | "new" -> isMutating command arguments && Option.isNone (tryOutput arguments)
        | _ -> false

module private StateVerification =
    let private solutionTarget target =
        target |> Option.defaultValue (Directory.GetCurrentDirectory())

    let private verifySolution targetPath cancellationToken =
        task {
            let! outcome = SolutionStore.OpenAsync(targetPath, cancellationToken)

            match outcome with
            | Success workspace -> return Ok(workspace)
            | Failure failure -> return Error failure.Diagnostic.Message
        }

    let private solutionProjectPaths (workspace: SolutionWorkspace) =
        workspace.RootProjection.Projects
        |> Seq.map (fun project -> project.Path.AbsolutePath.Value)
        |> Set.ofSeq

    let private requestedPaths (arguments: string list) =
        arguments
        |> List.takeWhile (fun argument -> not (argument.StartsWith("-", StringComparison.Ordinal)))
        |> List.map (fun argument -> Path.GetFullPath argument)

    let verify command arguments cancellationToken =
        task {
            match command, arguments with
            | ("solution" | "sln"), _ ->
                let target, operation, operands = ArgumentGrammar.solutionInvocation arguments
                let! workspace = verifySolution (solutionTarget target) cancellationToken

                match workspace, operation with
                | Error message, _ -> return Error message
                | Ok opened, Some "add" ->
                    let projects = solutionProjectPaths opened
                    let expected = requestedPaths operands

                    if expected |> List.forall projects.Contains then
                        return Ok(Some opened.WorkspaceDescriptor.WorkspaceRevision.Value)
                    else
                        return Error "The requested project was not present after the solution command."
                | Ok opened, Some "remove" ->
                    let projects = solutionProjectPaths opened
                    let expected = requestedPaths operands

                    if expected |> List.forall (fun project -> not (projects.Contains project)) then
                        return Ok(Some opened.WorkspaceDescriptor.WorkspaceRevision.Value)
                    else
                        return Error "The requested project remained after the solution command."
                | Ok opened, Some "migrate" ->
                    let migrated = Path.ChangeExtension(opened.BackingPath.Value, ".slnx")

                    if File.Exists migrated then
                        return Ok(Some opened.WorkspaceDescriptor.WorkspaceRevision.Value)
                    else
                        return Error "The migrated .slnx file was not created."
                | Ok opened, _ -> return Ok(Some opened.WorkspaceDescriptor.WorkspaceRevision.Value)
            | ("package" | "reference"), _ ->
                match ArgumentGrammar.tryProject arguments with
                | Some project when File.Exists project ->
                    let text = File.ReadAllText project
                    let operation = arguments |> List.tryHead |> Option.defaultValue ""

                    let subject =
                        arguments
                        |> List.skip 1
                        |> List.tryFind (fun value -> not (value.StartsWith("-", StringComparison.Ordinal)))

                    match command, operation, subject with
                    | "package", ("add" | "update"), Some package when text.Contains(package, StringComparison.Ordinal) ->
                        return Ok None
                    | "package", "remove", Some package when not (text.Contains(package, StringComparison.Ordinal)) ->
                        return Ok None
                    | "reference", "add", Some reference ->
                        let name = Path.GetFileName(reference) |> Option.ofObj |> Option.defaultValue ""

                        if text.Contains(name, StringComparison.Ordinal) then
                            return Ok None
                        else
                            return Error "The requested project mutation was not reflected in the project file."
                    | "reference", "remove", Some reference ->
                        let name = Path.GetFileName(reference) |> Option.ofObj |> Option.defaultValue ""

                        if not (text.Contains(name, StringComparison.Ordinal)) then
                            return Ok None
                        else
                            return Error "The requested project mutation was not reflected in the project file."
                    | _ -> return Error "The requested project mutation was not reflected in the project file."
                | Some _ -> return Error "The project selected for verification does not exist."
                | None -> return Error "This management command requires --project for verification."
            | "new", _ ->
                match ArgumentGrammar.tryOutput arguments with
                | Some output when Directory.Exists output -> return Ok None
                | Some _ -> return Error "The template output directory was not created."
                | None -> return Error "This template command requires --output for verification."
            | _ -> return Ok None
        }

module private ProcessExecution =
    let private ansi =
        Regex("\u001b(?:[@-_][0-?]*[ -/]*[@-~]|\\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled)

    let withoutAnsi (value: string) =
        ansi.Replace(value, String.Empty).Replace("\u001b", String.Empty)

    let run (host: string) (arguments: string list) (cancellationToken: CancellationToken) =
        task {
            let startInfo = ProcessStartInfo()
            startInfo.FileName <- host
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.CreateNoWindow <- true

            let prefix =
                Environment.GetEnvironmentVariable "DOTNET_PLUS_DOTNET_PREFIX" |> Option.ofObj

            prefix |> Option.iter startInfo.ArgumentList.Add
            arguments |> List.iter startInfo.ArgumentList.Add

            use childProcess = new Process(StartInfo = startInfo)

            try
                if not (childProcess.Start()) then
                    return Error(BrokerFailure.internalFailure "The dotnet host did not start.")
                else
                    let outputTask = childProcess.StandardOutput.ReadToEndAsync()
                    let errorTask = childProcess.StandardError.ReadToEndAsync()

                    try
                        do! childProcess.WaitForExitAsync(cancellationToken)
                        let! output = outputTask
                        let! error = errorTask
                        return Ok(childProcess.ExitCode, output, error)
                    with :? OperationCanceledException ->
                        if not childProcess.HasExited then
                            childProcess.Kill(true)

                        do! childProcess.WaitForExitAsync(CancellationToken.None)
                        let! _ = outputTask
                        let! _ = errorTask
                        return Error(BrokerFailure.cancelled ())
            with
            | :? OperationCanceledException -> return Error(BrokerFailure.cancelled ())
            | :? System.ComponentModel.Win32Exception ->
                return Error(BrokerFailure.internalFailure "The dotnet host could not be started.")
            | :? IOException -> return Error(BrokerFailure.internalFailure "The dotnet host could not be started.")
        }

[<AbstractClass; Sealed>]
type CliBroker private () =
    static member InternalFailure() =
        let failure =
            BrokerFailure.internalFailure "The CLI broker encountered an internal failure."

        { CommandId = ""
          Success = false
          Revision = None
          Result =
            { Summary = None
              ChildArguments = []
              StandardOutput = ""
              StandardError = "" }
          Diagnostics = [ BrokerFailure.toBrokerDiagnostic failure ]
          ExternalExitCode = None
          StandardOutput = ""
          StandardError = "" }

    static member ExecuteAsync(arguments: string array, cancellationToken: CancellationToken) : Task<BrokerResult> =
        task {
            let _, commandAndArguments = ArgumentGrammar.parse arguments

            let result
                commandId
                success
                summary
                diagnostics
                externalExitCode
                childArguments
                standardOutput
                standardError
                revision
                =
                { CommandId = commandId
                  Success = success
                  Revision = revision
                  Result =
                    { Summary = summary
                      ChildArguments = childArguments
                      StandardOutput = standardOutput
                      StandardError = standardError }
                  Diagnostics = diagnostics
                  ExternalExitCode = externalExitCode
                  StandardOutput = standardOutput
                  StandardError = standardError }

            match commandAndArguments with
            | [] ->
                let failure = BrokerFailure.invalid "A plus command is required."
                return result "" false None [ BrokerFailure.toBrokerDiagnostic failure ] None [] "" "" None
            | command :: remaining when not (ArgumentGrammar.isSupported command) ->
                let failure =
                    BrokerFailure.unsupported "This dotnet command is not supported by dotnet-plus."

                return result command false None [ BrokerFailure.toBrokerDiagnostic failure ] None [] "" "" None
            | command :: remaining when
                ArgumentGrammar.isMutating command remaining
                && ArgumentGrammar.containsSolutionFilter remaining
                ->
                let failure =
                    BrokerFailure.unsupported ".slnf files are read-only and cannot be mutated."

                return
                    result
                        (ArgumentGrammar.commandId command)
                        false
                        None
                        [ BrokerFailure.toBrokerDiagnostic failure ]
                        None
                        []
                        ""
                        ""
                        None
            | command :: remaining when ArgumentGrammar.requiresConcreteVerification command remaining ->
                let failure =
                    BrokerFailure.unsupported
                        "This management command shape cannot be verified; provide the documented target option."

                return
                    result
                        (ArgumentGrammar.commandId command)
                        false
                        None
                        [ BrokerFailure.toBrokerDiagnostic failure ]
                        None
                        []
                        ""
                        ""
                        None
            | command :: remaining ->
                match ArgumentGrammar.legacyDirectoryAdd command remaining with
                | Some(solutionPath, directoryPath) ->
                    let! legacy =
                        LegacySolutionCompatibilityEditor.AddDirectoryAsync(
                            solutionPath,
                            directoryPath,
                            cancellationToken
                        )

                    if legacy.ExitCode <> 0 then
                        let failure = BrokerFailure.external legacy.ExitCode
                        let message = legacy.Message |> Option.defaultValue failure.Diagnostic.Message

                        let diagnostic =
                            { BrokerFailure.toBrokerDiagnostic failure with
                                Message = message }

                        return result "solution" false None [ diagnostic ] (Some legacy.ExitCode) [] "" "" None
                    else
                        let! verified = StateVerification.verify "solution" [ solutionPath ] cancellationToken

                        match verified with
                        | Ok revision ->
                            return
                                result
                                    "solution"
                                    true
                                    (Some "legacy directory import verified")
                                    []
                                    (Some 0)
                                    []
                                    ""
                                    ""
                                    revision
                        | Error message ->
                            let failure = BrokerFailure.verification message

                            return
                                result
                                    "solution"
                                    false
                                    None
                                    [ BrokerFailure.toBrokerDiagnostic failure ]
                                    (Some 0)
                                    []
                                    ""
                                    ""
                                    None
                | None ->
                    let childArguments = ArgumentGrammar.childArguments command remaining

                    let host =
                        Environment.GetEnvironmentVariable "DOTNET_PLUS_DOTNET_HOST"
                        |> Option.ofObj
                        |> Option.defaultValue "dotnet"

                    let! execution = ProcessExecution.run host childArguments cancellationToken

                    match execution with
                    | Error failure ->
                        return
                            result
                                (ArgumentGrammar.commandId command)
                                false
                                None
                                [ BrokerFailure.toBrokerDiagnostic failure ]
                                None
                                childArguments
                                ""
                                ""
                                None
                    | Ok(exitCode, output, error) when exitCode <> 0 ->
                        let failure = BrokerFailure.external exitCode

                        return
                            result
                                (ArgumentGrammar.commandId command)
                                false
                                None
                                [ BrokerFailure.toBrokerDiagnostic failure ]
                                (Some exitCode)
                                childArguments
                                output
                                error
                                None
                    | Ok(exitCode, output, error) ->
                        let isHelp =
                            remaining
                            |> List.exists (fun argument -> argument = "--help" || argument = "-h" || argument = "-?")

                        let! verification =
                            if
                                (command = "solution" || command = "sln") && not isHelp
                                || ArgumentGrammar.isMutating command remaining
                            then
                                StateVerification.verify command remaining cancellationToken
                            else
                                Task.FromResult(Ok None)

                        match verification with
                        | Ok revision ->
                            return
                                result
                                    (ArgumentGrammar.commandId command)
                                    true
                                    (Some "dotnet command completed")
                                    []
                                    (Some exitCode)
                                    childArguments
                                    output
                                    error
                                    revision
                        | Error message ->
                            let failure = BrokerFailure.verification message

                            return
                                result
                                    (ArgumentGrammar.commandId command)
                                    false
                                    None
                                    [ BrokerFailure.toBrokerDiagnostic failure ]
                                    (Some exitCode)
                                    childArguments
                                    output
                                    error
                                    None
        }

    static member Render(result: BrokerResult, jsonMode: bool, output: TextWriter, error: TextWriter) =
        if jsonMode then
            let envelope =
                {| schemaVersion = 1
                   commandId = result.CommandId
                   success = result.Success
                   revision = result.Revision
                   result = result.Result
                   diagnostics = result.Diagnostics
                   externalExitCode = result.ExternalExitCode |}

            let options =
                System.Text.Json.JsonSerializerOptions(
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                )

            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(envelope, options))
        else
            let sanitize =
                if Console.IsOutputRedirected || not (Object.ReferenceEquals(output, Console.Out)) then
                    ProcessExecution.withoutAnsi
                else
                    id

            if not (String.IsNullOrEmpty result.StandardOutput) then
                output.Write(sanitize result.StandardOutput)

            if not (String.IsNullOrEmpty result.StandardError) then
                error.Write(sanitize result.StandardError)

            result.Diagnostics
            |> List.iter (fun diagnostic -> error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}"))

        if result.Success then
            0
        else
            result.ExternalExitCode |> Option.defaultValue 1
