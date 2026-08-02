namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open System
open System.IO
open Dotnet.WorkspaceExplorer.ProjectEvaluation

module internal ProjectInputClassification =
    let private isSameOrDescendant root candidate =
        let relative = Path.GetRelativePath(root, candidate)

        relative = "."
        || (not (Path.IsPathRooted relative)
            && relative <> ".."
            && not (
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            )
            && not (
                relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            ))

    let private rootProperties =
        [ "DotNetRoot"; "MSBuildToolsPath"; "NetCoreRoot"; "NuGetPackageRoot" ]

    let toolchainRoots (snapshot: ProjectEvaluationSnapshot) =
        snapshot.Dimensions
        |> Seq.collect _.Properties
        |> Seq.filter (fun property ->
            rootProperties
            |> List.exists (fun name ->
                String.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
        |> Seq.choose (fun property ->
            if String.IsNullOrWhiteSpace property.Value then
                None
            else
                try
                    Some(Path.GetFullPath property.Value)
                with
                | :? ArgumentException
                | :? NotSupportedException
                | :? PathTooLongException -> None)
        |> Seq.distinct
        |> Seq.toArray

    let isToolchainPath roots path =
        roots |> Seq.exists (fun root -> isSameOrDescendant root path)
