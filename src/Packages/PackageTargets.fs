namespace Dotnet.WorkspaceExplorer.Packages

open System
open System.IO

[<RequireQualifiedAccess>]
type PackageProjectLanguage =
    | CSharp
    | FSharp
    | VisualBasic

[<RequireQualifiedAccess>]
type PackageWorkspaceTargetKind =
    | Solution
    | SolutionXml
    | SolutionFilter
    | Project of PackageProjectLanguage
    | Directory

type PackageWorkspaceTarget =
    private
        { Path: string
          Kind: PackageWorkspaceTargetKind }

[<RequireQualifiedAccess>]
module PackageWorkspaceTarget =
    let private absolutePath value =
        if String.IsNullOrWhiteSpace value then
            Error(PackageContractViolation.MissingValue "target")
        else
            try
                Ok(Path.GetFullPath value)
            with
            | :? ArgumentException
            | :? NotSupportedException
            | :? PathTooLongException -> Error(PackageContractViolation.InvalidValue "target")

    let private extensionKind (path: string) =
        let extension =
            Path.GetExtension path
            |> Option.ofObj
            |> Option.defaultValue String.Empty
            |> _.ToLowerInvariant()

        match extension with
        | ".sln" -> Some PackageWorkspaceTargetKind.Solution
        | ".slnx" -> Some PackageWorkspaceTargetKind.SolutionXml
        | ".slnf" -> Some PackageWorkspaceTargetKind.SolutionFilter
        | ".csproj" -> Some(PackageWorkspaceTargetKind.Project PackageProjectLanguage.CSharp)
        | ".fsproj" -> Some(PackageWorkspaceTargetKind.Project PackageProjectLanguage.FSharp)
        | ".vbproj" -> Some(PackageWorkspaceTargetKind.Project PackageProjectLanguage.VisualBasic)
        | _ -> None

    let file value =
        absolutePath value
        |> Result.bind (fun path ->
            match extensionKind path with
            | Some kind -> Ok { Path = path; Kind = kind }
            | None -> Error(PackageContractViolation.InvalidValue "target"))

    let directory value =
        absolutePath value
        |> Result.map (fun path ->
            { Path = path
              Kind = PackageWorkspaceTargetKind.Directory })

    let path target = target.Path
    let kind target = target.Kind

[<RequireQualifiedAccess>]
type PackageTargetScope =
    | Project of PackageProjectId
    | Framework of project: PackageProjectId * framework: TargetFramework
    | Runtime of
        project: PackageProjectId *
        framework: TargetFramework *
        runtimeIdentifier: RuntimeIdentifier
