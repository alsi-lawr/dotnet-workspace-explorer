namespace Dotnet.WorkspaceExplorer

open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Packages

open System
open System.Reflection

[<RequireQualifiedAccess>]
module internal CommandLineInformation =
    let help =
        [ "Usage:"
          "  dotnet-we [--json] solution|sln <SLN_FILE> launch list"
          "  dotnet-we [--json] solution|sln <SLN_FILE> launch set <NAME> [<PROJECT>...]"
          "  dotnet-we [--json] solution|sln <SLN_FILE> launch remove <NAME>"
          "  dotnet-we [--json] solution|sln <SLN_FILE> add directory|dir <DIRECTORY>"
          "  dotnet-we workspace <TARGET> --pipe [--export-workers <COUNT>]"
          "  dotnet-we packages <TARGET> --pipe"
          ""
          "Options:"
          "  -h, --help       Show command-line help."
          "  -v, --version    Show the installed version." ]
        |> String.concat Environment.NewLine

    let version =
        let assembly = Assembly.GetExecutingAssembly()

        let assemblyVersion =
            assembly.GetName().Version
            |> Option.ofObj
            |> Option.map (fun version -> version.ToString(3))
            |> Option.defaultValue "0.0.0"

        match assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null -> assemblyVersion
        | attribute -> attribute.InformationalVersion

[<RequireQualifiedAccess>]
type internal ProgramInvocation =
    | Help
    | Version
    | ProjectEvaluationHost of sdkPath: string
    | PackagePipe of target: PackageWorkspaceTarget
    | InvalidPackageStartup of failure: PackageStartupFailure
    | ExistingRoute of arguments: string array

[<RequireQualifiedAccess>]
module internal ProgramInvocation =
    let parse currentDirectory arguments =
        match arguments with
        | [| "-h" |]
        | [| "--help" |] -> ProgramInvocation.Help
        | [| "-v" |]
        | [| "--version" |] -> ProgramInvocation.Version
        | [| "internal"; "project-evaluation-host"; "--sdk"; sdkPath |] ->
            ProgramInvocation.ProjectEvaluationHost sdkPath
        | _ ->
            match PackageStartup.resolve currentDirectory arguments with
            | PackageStartup.Pipe target -> ProgramInvocation.PackagePipe target
            | PackageStartup.Invalid failure -> ProgramInvocation.InvalidPackageStartup failure
            | PackageStartup.NotPackageRoute -> ProgramInvocation.ExistingRoute arguments
