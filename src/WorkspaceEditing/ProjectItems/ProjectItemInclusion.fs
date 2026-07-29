namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.ProjectEvaluation

#nowarn "3261"

open System
open System.IO
open System.Text.RegularExpressions

module internal ProjectItemInclusion =
    open MsBuildProjectDocument
    let itemTypes = Set.ofList [ "Compile"; "Content"; "None"; "EmbeddedResource" ]

    let metadataNames =
        Set.ofList
            [ "Link"
              "DependentUpon"
              "Visible"
              "CopyToOutputDirectory"
              "CopyToPublishDirectory"
              "Generator"
              "LastGenOutput"
              "CustomToolNamespace" ]

    let projectDirectory (project: SolutionProject) =
        Path.GetDirectoryName project.Path.AbsolutePath.Value
        |> Option.ofObj
        |> Option.defaultValue (Directory.GetCurrentDirectory())

    let relativePath directory (path: WorkspaceArtifactPath) =
        Path
            .GetRelativePath(directory, path.Value)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')

    let external directory path =
        let relative = Path.GetRelativePath(directory, path)

        Path.IsPathRooted relative
        || relative = ".."
        || relative.StartsWith $"..{Path.DirectorySeparatorChar}"
        || relative.StartsWith $"..{Path.AltDirectorySeparatorChar}"

    let evaluatedAs (snapshot: ProjectEvaluationSnapshot) (itemType: string) (path: string) =
        snapshot.Dimensions
        |> Seq.collect _.Items
        |> Seq.exists (fun item ->
            item.ItemType = itemType
            && not (isNull item.ResolvedPath)
            && item.ResolvedPath.Value = path)

    let globMatches (pattern: string) (value: string) =
        let normalize (path: string) =
            path.Trim().Replace('\\', '/')
            |> fun value -> Regex.Replace(value, "/+", "/")
            |> fun value ->
                if value.StartsWith("./", StringComparison.Ordinal) then
                    value[2..]
                else
                    value

        let normalized = normalize pattern

        if
            String.IsNullOrWhiteSpace normalized
            || normalized.Contains("$(", StringComparison.Ordinal)
        then
            false
        else
            let source =
                Regex
                    .Escape(normalized)
                    .Replace("\\*\\*/", "(?:.*/)?")
                    .Replace("\\*\\*", ".*")
                    .Replace("\\*", "[^/]*")
                    .Replace("\\?", "[^/]")

            Regex.IsMatch(
                normalize value,
                $"^{source}$",
                RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant
            )

    let defaultItemType (snapshot: ProjectEvaluationSnapshot) (path: string) =
        let projectDirectory =
            Path.GetDirectoryName snapshot.ProjectPath.Value
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        if external projectDirectory path then
            None
        else
            let relative = relativePath projectDirectory (WorkspaceArtifactPath.Create path)
            let absolute = Path.GetFullPath(path).Replace('\\', '/')
            let extension = Path.GetExtension(path).ToLowerInvariant()

            let enabled (dimension: ProjectEvaluationDimension) name =
                dimension.Properties
                |> Seq.filter (fun property -> property.Name = name)
                |> Seq.tryLast
                |> Option.map (fun property ->
                    not (
                        String.Equals(property.Value, "false", StringComparison.OrdinalIgnoreCase)
                    ))
                |> Option.defaultValue true

            let uses (dimension: ProjectEvaluationDimension) name =
                dimension.Properties
                |> Seq.exists (fun property ->
                    property.Name = name
                    && String.Equals(property.Value, "true", StringComparison.OrdinalIgnoreCase))

            let excluded (names: Set<string>) (dimension: ProjectEvaluationDimension) =
                dimension.Properties
                |> Seq.filter (fun property -> names.Contains property.Name)
                |> Seq.groupBy _.Name
                |> Seq.collect (fun (_, properties) ->
                    properties
                    |> Seq.last
                    |> fun property ->
                        property.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                |> Seq.exists (fun pattern ->
                    let pattern = pattern.Trim()

                    if Path.IsPathRooted pattern then
                        globMatches pattern absolute
                    else
                        globMatches pattern relative)

            let ordinaryExcludes =
                Set.ofList
                    [ "DefaultItemExcludes"
                      "DefaultItemExcludesInProjectFolder"
                      "DefaultExcludesInProjectFolder" ]

            let defaultItemExcludes = Set.singleton "DefaultItemExcludes"

            let webContentExcludes =
                Set.union ordinaryExcludes (Set.singleton "DefaultWebContentItemExcludes")

            let inDirectory directory =
                relative.Equals(directory, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith($"{directory}/", StringComparison.OrdinalIgnoreCase)

            let contentExtension = Set.contains extension (Set.ofList [ ".json"; ".config" ])

            let included (dimension: ProjectEvaluationDimension) =
                let defaultItems = enabled dimension "EnableDefaultItems"
                let compileItems = enabled dimension "EnableDefaultCompileItems"
                let embeddedResourceItems = enabled dimension "EnableDefaultEmbeddedResourceItems"
                let noneItems = enabled dimension "EnableDefaultNoneItems"
                let contentItems = enabled dimension "EnableDefaultContentItems"

                let workerJsonOrConfig =
                    uses dimension "UsingMicrosoftNETSdkWorker" && contentExtension

                let webWwwRoot = uses dimension "UsingMicrosoftNETSdkWeb" && inDirectory "wwwroot"

                let webJsonOrConfig = uses dimension "UsingMicrosoftNETSdkWeb" && contentExtension

                let razorFile =
                    uses dimension "UsingMicrosoftNETSdkRazor"
                    && Set.contains extension (Set.ofList [ ".cshtml"; ".razor" ])

                let contentDefault =
                    workerJsonOrConfig && not (excluded ordinaryExcludes dimension)
                    || (webWwwRoot
                        && if inDirectory "wwwroot/.well-known" then
                               not (excluded defaultItemExcludes dimension)
                           else
                               not (excluded ordinaryExcludes dimension))
                    || webJsonOrConfig && not (excluded webContentExcludes dimension)
                    || razorFile && not (excluded webContentExcludes dimension)

                let hasContentDefault =
                    workerJsonOrConfig || webWwwRoot || webJsonOrConfig || razorFile

                if not defaultItems then
                    None
                elif contentItems && hasContentDefault then
                    if contentDefault then Some "Content" else None
                elif excluded ordinaryExcludes dimension then
                    None
                elif
                    compileItems && Set.contains extension (Set.ofList [ ".cs"; ".fs"; ".vb" ])
                then
                    Some "Compile"
                elif
                    embeddedResourceItems
                    && Set.contains extension (Set.ofList [ ".resx"; ".resw" ])
                then
                    Some "EmbeddedResource"
                elif noneItems then
                    Some "None"
                else
                    None

            snapshot.Dimensions
            |> Seq.map included
            |> Seq.distinct
            |> Seq.toArray
            |> function
                | [||] -> None
                | [| value |] -> value
                | _ ->
                    raise (
                        ArgumentException
                            "The default item policy conflicts across evaluation dimensions."
                    )

    let defaultItemPolicy snapshot itemType path =
        defaultItemType snapshot path = Some itemType

    let appendRequestedItem document snapshot itemType path includeValue =
        match defaultItemType snapshot path with
        | Some defaultType when defaultType = itemType -> false
        | Some defaultType ->
            appendRemove document defaultType includeValue
            appendItem document itemType includeValue []
            true
        | None ->
            appendItem document itemType includeValue []
            true

    let generated (directory: string) (path: string) =
        let relative = Path.GetRelativePath(directory, path).Replace('\\', '/')

        relative.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
        || relative.Equals(".generated", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(".generated/", StringComparison.OrdinalIgnoreCase)
        || relative.EndsWith("/.generated", StringComparison.OrdinalIgnoreCase)
        || relative.Contains("/.generated/", StringComparison.OrdinalIgnoreCase)

    let effectiveItemTypes (snapshot: ProjectEvaluationSnapshot) includeValue path =
        snapshot.Dimensions
        |> Seq.collect _.Items
        |> Seq.filter (fun item ->
            item.EvaluatedInclude = includeValue
            || not (isNull item.ResolvedPath) && item.ResolvedPath.Value = path)
        |> Seq.map _.ItemType
        |> Seq.filter (fun itemType -> itemTypes.Contains itemType)
        |> Seq.distinct
        |> Seq.toArray

    let effectiveItemType snapshot includeValue path =
        let types = effectiveItemTypes snapshot includeValue path

        if types.Length <> 1 then
            raise (ArgumentException "The effective item type is ambiguous.")

        types[0]
