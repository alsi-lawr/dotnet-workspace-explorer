namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Packages

[<RequireQualifiedAccess>]
type internal ProgramInvocation =
    | ProjectEvaluationHost of sdkPath: string
    | PackageTerminal of target: PackageWorkspaceTarget
    | PackagePipe of target: PackageWorkspaceTarget
    | InvalidPackageStartup of failure: PackageStartupFailure
    | ExistingRoute of arguments: string array

[<RequireQualifiedAccess>]
module internal ProgramInvocation =
    let parse currentDirectory arguments =
        match arguments with
        | [| "internal"; "project-evaluation-host"; "--sdk"; sdkPath |] ->
            ProgramInvocation.ProjectEvaluationHost sdkPath
        | _ ->
            match PackageStartup.resolve currentDirectory arguments with
            | PackageStartup.Terminal target -> ProgramInvocation.PackageTerminal target
            | PackageStartup.Pipe target -> ProgramInvocation.PackagePipe target
            | PackageStartup.Invalid failure -> ProgramInvocation.InvalidPackageStartup failure
            | PackageStartup.NotPackageRoute -> ProgramInvocation.ExistingRoute arguments
