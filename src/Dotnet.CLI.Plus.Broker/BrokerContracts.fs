namespace Dotnet.CLI.Plus

#nowarn "3261"
#nowarn "3511"

open System
open System.IO
open System.Text
open Dotnet.CLI.Plus.Core

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
          { PlusGrammar =
              "[--json] new "
              + "[template|create|list|search|details|install|uninstall|update] [options]"
            ChildArguments = "new ..."
            PassThroughOptions =
              "--output/-o and --dry-run are inspected without changing child argv."
            UnsupportedCases = "Template state that cannot be deterministically refreshed." }
          { PlusGrammar = "[--json] restore|build|test|run [options]"
            ChildArguments = "same command and arguments"
            PassThroughOptions = "All child argv is preserved exactly."
            UnsupportedCases = "Lifecycle policy and orchestration (T-011)." } ]

type internal SolutionOperation =
    | Add
    | List
    | Remove
    | Migrate

type internal PackageOperation =
    | PackageAdd
    | PackageList
    | PackageRemove
    | PackageUpdate
    | PackageSearch
    | PackageDownload

type internal ReferenceOperation =
    | ReferenceAdd
    | ReferenceList
    | ReferenceRemove

type internal NewOperation =
    | TemplateCreate
    | TemplateList
    | TemplateSearch
    | TemplateDetails
    | TemplateInstall
    | TemplateUninstall
    | TemplateUpdate

type internal ParsedCommand =
    | Solution of
        target: string option *
        operation: SolutionOperation option *
        operands: string list *
        help: bool
    | Package of
        operation: PackageOperation option *
        project: string option *
        file: string option *
        version: string option *
        framework: string option *
        operands: string list *
        verificationAmbiguous: bool *
        help: bool
    | Reference of
        operation: ReferenceOperation option *
        project: string option *
        framework: string option *
        operands: string list *
        verificationAmbiguous: bool *
        help: bool
    | New of
        operation: NewOperation *
        output: string option *
        dryRun: bool *
        operands: string list *
        help: bool
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
        pending.Append value |> ignore
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
                            || source[endIndex] = '\u001b'
                               && endIndex + 1 < source.Length
                               && source[endIndex + 1] = '\\'

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
                    || character >= ' ' && character <> '\u007f'
                then
                    output.Append character |> ignore

                index <- index + 1

        pending.Clear() |> ignore

        if incomplete >= 0 then
            pending.Append(source.Substring incomplete) |> ignore

        output.ToString()

    member _.Complete() =
        pending.Clear() |> ignore
        String.Empty

module internal BrokerFailure =
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

    let terminationIncomplete () =
        PartialRecoveryRequired(
            "Terminate remaining descendant processes manually.",
            diagnostic
                WorkspaceErrorCode.PartialRecoveryRequired.Value
                ("The command process exited, but the full descendant process tree "
                 + "could not be confirmed terminated.")
                false
        )

    let internalFailure message =
        Internal(diagnostic WorkspaceErrorCode.InternalError.Value message false)
