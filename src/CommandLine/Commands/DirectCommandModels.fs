namespace Dotnet.WorkspaceExplorer.CommandLine

open Dotnet.WorkspaceExplorer.Workspaces

#nowarn "3261"
#nowarn "3511"

type internal LaunchProfileCommand =
    | LaunchList
    | LaunchSet
    | LaunchRemove

type internal DirectCommand =
    | LaunchProfile of
        target: string *
        operation: LaunchProfileCommand *
        name: string option *
        projects: string list
    | ImportDirectory of solution: string * directory: string

type internal DirectCommandCompletion =
    { CommandId: string
      Revision: WorkspaceRevision option
      Output: string option }

type internal DirectCommandFailure =
    { CommandId: string
      Diagnostic: WorkspaceDiagnostic }
