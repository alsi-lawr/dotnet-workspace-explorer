namespace Dotnet.WorkspaceExplorer.CommandLine

open Dotnet.WorkspaceExplorer.Workspaces

#nowarn "3261"
#nowarn "3511"

open System.IO

type internal SolutionCommand =
    | Add
    | List
    | Remove
    | Migrate

type internal LaunchProfileCommand =
    | LaunchList
    | LaunchSet
    | LaunchRemove

type internal PackageCommand =
    | PackageAdd
    | PackageList
    | PackageRemove
    | PackageUpdate
    | PackageSearch
    | PackageDownload

type internal ReferenceCommand =
    | ReferenceAdd
    | ReferenceList
    | ReferenceRemove

type internal TemplateCommand =
    | TemplateCreate
    | TemplateList
    | TemplateSearch
    | TemplateDetails
    | TemplateInstall
    | TemplateUninstall
    | TemplateUpdate

type internal DirectCommand =
    | Solution of
        target: string option *
        operation: SolutionCommand option *
        operands: string list *
        help: bool
    | Package of
        operation: PackageCommand option *
        project: string option *
        file: string option *
        version: string option *
        framework: string option *
        operands: string list *
        verificationAmbiguous: bool *
        help: bool
    | Reference of
        operation: ReferenceCommand option *
        project: string option *
        framework: string option *
        operands: string list *
        verificationAmbiguous: bool *
        help: bool
    | New of
        operation: TemplateCommand *
        output: string option *
        dryRun: bool *
        operands: string list *
        help: bool
    | Lifecycle of command: string * help: bool
    | LaunchProfile of
        target: string *
        operation: LaunchProfileCommand *
        name: string option *
        projects: string list *
        help: bool

type internal DotnetHost =
    { FileName: string
      Prefix: string list }

type internal CommandOutputMode =
    | Human of TextWriter * TextWriter * bool * bool
    | Json

type internal DirectCommandOutput =
    { Summary: string option
      ChildArguments: string list
      StandardOutput: string
      StandardError: string }

type internal DirectCommandResult =
    { CommandId: string
      Success: bool
      Revision: WorkspaceRevision option
      Payload: DirectCommandOutput
      Diagnostics: WorkspaceDiagnostic list
      ExternalExitCode: int option }
