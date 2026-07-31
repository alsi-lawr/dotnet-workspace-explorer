namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.Workspaces

[<RequireQualifiedAccess>]
type internal DependencyReferenceKind =
    | Project
    | Assembly
    | Analyzer

type internal DependencyDetail =
    { DetailId: string
      Label: string
      Value: string }

[<RequireQualifiedAccess>]
module internal DependencyDetails =
    let private detail key label value =
        { DetailId = key
          Label = label
          Value = value }

    let private optionalDetail key label value =
        value
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.map (detail key label)

    let private metadataValue itemType includeValue name (dimension: ProjectEvaluationDimension) =
        dimension.Items
        |> Seq.tryFind (fun item ->
            String.Equals(item.ItemType, itemType, StringComparison.Ordinal)
            && String.Equals(item.EvaluatedInclude, includeValue, StringComparison.Ordinal))
        |> Option.bind (fun item ->
            item.Metadata
            |> Seq.tryFind (fun metadata ->
                String.Equals(metadata.Name, name, StringComparison.OrdinalIgnoreCase))
            |> Option.map _.Value)
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let private metadataDetail
        itemType
        includeValue
        metadataName
        key
        label
        (dimension: ProjectEvaluationDimension)
        =
        metadataValue itemType includeValue metadataName dimension
        |> optionalDetail key label

    let private displayBoolean (value: string) =
        match Boolean.TryParse value with
        | true, parsed -> if parsed then "True" else "False"
        | _ -> value

    let private booleanMetadataDetail
        itemType
        includeValue
        metadataName
        key
        label
        (dimension: ProjectEvaluationDimension)
        =
        metadataValue itemType includeValue metadataName dimension
        |> Option.map displayBoolean
        |> optionalDetail key label

    let private assembly (path: WorkspaceArtifactPath) =
        if not (File.Exists path.Value) then
            Array.empty
        else
            try
                let identity = AssemblyName.GetAssemblyName path.Value

                let runtimeVersion =
                    use stream =
                        File.Open(
                            path.Value,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite ||| FileShare.Delete
                        )

                    use pe = new PEReader(stream)

                    if pe.HasMetadata then
                        pe.GetMetadataReader().MetadataVersion.TrimEnd(char 0) |> Some
                    else
                        None

                let publicKeyToken =
                    identity.GetPublicKeyToken()
                    |> Option.ofObj
                    |> Option.filter (fun value -> value.Length > 0)

                [| yield!
                       identity.Name
                       |> Option.ofObj
                       |> optionalDetail "identity" "Identity"
                       |> Option.toList

                   yield!
                       identity.Version
                       |> Option.ofObj
                       |> Option.map string
                       |> optionalDetail "version" "Version"
                       |> Option.toList

                   yield!
                       runtimeVersion
                       |> optionalDetail "runtime-version" "Runtime Version"
                       |> Option.toList

                   yield
                       identity.CultureName
                       |> Option.ofObj
                       |> Option.filter (String.IsNullOrWhiteSpace >> not)
                       |> Option.defaultValue "neutral"
                       |> detail "culture" "Culture"

                   yield!
                       publicKeyToken
                       |> Option.map (fun value -> Convert.ToHexString(value).ToLowerInvariant())
                       |> optionalDetail "public-key-token" "Public Key Token"
                       |> Option.toList

                   yield
                       detail
                           "strong-name"
                           "Strong Name"
                           (if publicKeyToken.IsSome then "True" else "False") |]
            with
            | :? ArgumentException
            | :? BadImageFormatException
            | :? FileLoadException
            | :? IOException
            | :? NotSupportedException
            | :? UnauthorizedAccessException -> Array.empty

    let reference
        kind
        includeValue
        (resolved: WorkspaceArtifactPath option)
        (dimension: ProjectEvaluationDimension)
        =
        let kindName, itemType, hasAssemblyIdentity =
            match kind with
            | DependencyReferenceKind.Project -> "Project", "ProjectReference", false
            | DependencyReferenceKind.Assembly -> "Assembly", "Reference", true
            | DependencyReferenceKind.Analyzer -> "Analyzer", "Analyzer", true

        let pathDetails =
            match resolved with
            | Some path ->
                [| detail "resolved" "Resolved" "True"; detail "path" "Path" path.Value |]
            | None -> [| detail "resolved" "Resolved" "False" |]

        [| yield detail "type" "Type" kindName
           yield! pathDetails

           if hasAssemblyIdentity then
               yield! resolved |> Option.map assembly |> Option.defaultValue Array.empty

           yield!
               [| metadataDetail itemType includeValue "Aliases" "aliases" "Aliases" dimension
                  booleanMetadataDetail
                      itemType
                      includeValue
                      "Private"
                      "copy-local"
                      "Copy Local"
                      dimension
                  booleanMetadataDetail
                      itemType
                      includeValue
                      "EmbedInteropTypes"
                      "embed-interop-types"
                      "Embed Interop Types"
                      dimension
                  booleanMetadataDetail
                      itemType
                      includeValue
                      "SpecificVersion"
                      "specific-version"
                      "Specific Version"
                      dimension
                  booleanMetadataDetail
                      itemType
                      includeValue
                      "ReferenceOutputAssembly"
                      "reference-output-assembly"
                      "Reference Output Assembly"
                      dimension
                  metadataDetail
                      itemType
                      includeValue
                      "PrivateAssets"
                      "private-assets"
                      "Private Assets"
                      dimension |]
               |> Array.choose id |]

    let project path =
        [| detail "type" "Type" "Project"
           detail "resolved" "Resolved" "True"
           detail "path" "Path" path |]

    let private packagePath
        (packageId: string)
        (version: string)
        (dimension: ProjectEvaluationDimension)
        =
        dimension.Properties
        |> Seq.tryFind (fun property ->
            String.Equals(property.Name, "NuGetPackageRoot", StringComparison.OrdinalIgnoreCase))
        |> Option.map _.Value
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.bind (fun root ->
            try
                let candidate =
                    Path.GetFullPath(
                        Path.Combine(
                            root,
                            packageId.ToLowerInvariant(),
                            version.ToLowerInvariant()
                        )
                    )

                if Directory.Exists candidate then Some candidate else None
            with
            | :? ArgumentException
            | :? NotSupportedException
            | :? PathTooLongException -> None)

    let package (package: EvaluatedPackage) dimension =
        let version =
            Option.ofObj package.Version |> Option.filter (String.IsNullOrWhiteSpace >> not)

        [| yield detail "type" "Type" "Package"
           yield detail "package-id" "ID" package.Id

           yield! version |> optionalDetail "version" "Version" |> Option.toList

           yield!
               version
               |> Option.bind (fun value -> packagePath package.Id value dimension)
               |> optionalDetail "path" "Path"
               |> Option.toList

           yield!
               [| metadataDetail
                      "PackageReference"
                      package.Id
                      "PrivateAssets"
                      "private-assets"
                      "Private Assets"
                      dimension
                  metadataDetail
                      "PackageReference"
                      package.Id
                      "IncludeAssets"
                      "include-assets"
                      "Include Assets"
                      dimension
                  metadataDetail
                      "PackageReference"
                      package.Id
                      "ExcludeAssets"
                      "exclude-assets"
                      "Exclude Assets"
                      dimension
                  metadataDetail
                      "PackageReference"
                      package.Id
                      "Aliases"
                      "aliases"
                      "Aliases"
                      dimension |]
               |> Array.choose id |]
