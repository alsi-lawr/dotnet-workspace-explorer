namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System
open Dotnet.WorkspaceExplorer.Packages
open NuGet.Protocol.Core.Types

type internal MetadataLimits =
    {
        PackageId: int
        Version: int
        Description: int
        Summary: int
        Person: int
        People: int
        Tag: int
        Tags: int
        License: int
        Link: int
        DeprecationReason: int
        /// Maximum package versions accepted from one remote metadata response.
        AvailableVersions: int
        /// Maximum framework dependency groups accepted for one package version.
        DependencyGroups: int
        /// Maximum dependencies accepted in one framework group.
        DependenciesPerGroup: int
    }

[<RequireQualifiedAccess>]
module internal PackageMetadata =
    type private ResultBuilder() =
        member _.Bind(value, binding) = Result.bind binding value
        member _.Return value = Ok value
        member _.ReturnFrom value = value

    let private result = ResultBuilder()

    let limits =
        { PackageId = 100
          Version = 128
          Description = 4096
          Summary = 1024
          Person = 128
          People = 32
          Tag = 64
          Tags = 64
          License = 2048
          Link = 2048
          DeprecationReason = 256
          AvailableVersions = 512
          DependencyGroups = 128
          DependenciesPerGroup = 512 }

    let private boundedText limit (value: string) =
        if String.IsNullOrWhiteSpace value then
            None
        else
            let normalized =
                value
                |> Seq.map (fun character -> if Char.IsControl character then ' ' else character)
                |> Seq.toArray
                |> String
                |> fun text -> text.Trim()

            if String.IsNullOrEmpty normalized then None
            elif normalized.Length <= limit then Some normalized
            else Some(normalized.Substring(0, limit))

    let text limit value = boundedText limit value

    let values (separator: char array) limit count (value: string) =
        boundedText (limit * count) value
        |> Option.map (fun text ->
            text.Split(
                separator,
                StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries
            )
            |> Seq.choose (boundedText limit)
            |> Seq.distinct
            |> Seq.truncate count
            |> Seq.toList)
        |> Option.defaultValue []

    let people value =
        values [| ','; ';' |] limits.Person limits.People value

    let tags value =
        values [| ' '; ','; ';'; '\t'; '\r'; '\n' |] limits.Tag limits.Tags value

    let packageId value =
        boundedText limits.PackageId value
        |> function
            | Some text -> Ok text
            | None -> Error(PackageContractViolation.MissingValue "packageId")
        |> Result.bind PackageId.create

    let version (value: NuGet.Versioning.NuGetVersion) =
        boundedText limits.Version (value.ToNormalizedString())
        |> function
            | Some text -> Ok text
            | None -> Error(PackageContractViolation.MissingValue "version")
        |> Result.bind NuGetVersion.create

    let versionRange (value: NuGet.Versioning.VersionRange) =
        boundedText limits.Version (value.ToNormalizedString())
        |> function
            | Some text -> Ok text
            | None -> Error(PackageContractViolation.MissingValue "versionRange")
        |> Result.bind NuGetVersionRange.create

    let safeUri (value: Uri | null) : Uri option =
        match Option.ofObj value with
        | None -> None
        | Some uri when
            uri.IsAbsoluteUri
            && uri.OriginalString.Length <= limits.Link
            && String.IsNullOrEmpty uri.UserInfo
            && String.IsNullOrEmpty uri.Query
            && String.IsNullOrEmpty uri.Fragment
            && (uri.Scheme = Uri.UriSchemeHttps || uri.Scheme = Uri.UriSchemeHttp)
            ->
            Some uri
        | Some _ -> None

    let safeUriText value =
        match boundedText limits.Link value with
        | Some text ->
            match Uri.TryCreate(text, UriKind.Absolute) with
            | true, uri -> safeUri uri
            | _ -> None
        | None -> None

    let safeTextOrUri limit value =
        boundedText limit value
        |> Option.bind (fun text ->
            match Uri.TryCreate(text, UriKind.Absolute) with
            | true, uri -> safeUri uri |> Option.map _.OriginalString
            | _ -> Some text)

    let availableVersions values =
        values |> Seq.truncate limits.AvailableVersions

    let dependencyGroups values =
        values |> Seq.truncate limits.DependencyGroups

    let dependencies values =
        values |> Seq.truncate limits.DependenciesPerGroup

    let mergeDependencies current additions =
        Seq.append current additions |> dependencies |> Seq.toList

    let summary source (metadata: IPackageSearchMetadata) =
        result {
            let! identity = packageId metadata.Identity.Id
            let! packageVersion = version metadata.Identity.Version

            return
                { Identity = identity
                  Version = packageVersion
                  Description = boundedText limits.Description metadata.Description
                  Summary = boundedText limits.Summary metadata.Summary
                  Tags = tags metadata.Tags
                  Authors = people metadata.Authors
                  Owners =
                    if isNull metadata.OwnersList then
                        people metadata.Owners
                    else
                        metadata.OwnersList
                        |> Seq.choose (boundedText limits.Person)
                        |> Seq.distinct
                        |> Seq.truncate limits.People
                        |> Seq.toList
                  Source = source }
        }
