namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System
open System.IO
open Dotnet.WorkspaceExplorer.Packages
open NuGet.Configuration
open NuGet.Protocol
open NuGet.Protocol.Core.Types

type internal ConfiguredSource =
    { Model: Dotnet.WorkspaceExplorer.Packages.PackageSource
      Configuration: NuGet.Configuration.PackageSource
      Repository: SourceRepository }

type internal ConfiguredCatalog =
    { Sources: ConfiguredSource list
      Mapping: PackageSourceMapping
      ConfigFiles: string list }

[<RequireQualifiedAccess>]
module internal NuGetSources =
    type private ResultBuilder() =
        member _.Bind(value, binding) = Result.bind binding value
        member _.Return value = Ok value
        member _.ReturnFrom value = value

    let private result = ResultBuilder()

    let private targetRoot (target: PackageWorkspaceTarget) =
        let path = PackageWorkspaceTarget.path target

        match PackageWorkspaceTarget.kind target with
        | PackageWorkspaceTargetKind.Directory -> path
        | _ ->
            Path.GetDirectoryName path
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

    let private rawSourceUri (root: string) (source: NuGet.Configuration.PackageSource) =
        match source.TrySourceAsUri with
        | null ->
            let path =
                if Path.IsPathRooted source.Source then
                    source.Source
                else
                    Path.Combine(root, source.Source)

            Uri(Path.GetFullPath path)
        | uri -> uri

    let private publicSourceUri root source =
        let raw = rawSourceUri root source

        if raw.IsAbsoluteUri then
            let redacted = UriBuilder raw
            redacted.UserName <- String.Empty
            redacted.Password <- String.Empty
            redacted.Query <- String.Empty
            redacted.Fragment <- String.Empty
            redacted.Uri
        else
            let redactedPath = raw.OriginalString.Split([| '?'; '#' |], 2)[0]

            let path =
                if Path.IsPathRooted redactedPath then
                    redactedPath
                else
                    Path.Combine(root, redactedPath)

            Uri(Path.GetFullPath path)

    let loadCatalog (target: PackageWorkspaceTarget) =
        try
            let root = targetRoot target
            let settings = Settings.LoadDefaultSettings root
            let sourceProvider = PackageSourceProvider settings

            let repositoryProvider =
                SourceRepositoryProvider(sourceProvider, Repository.Provider.GetCoreV3())

            let repositories =
                repositoryProvider.GetRepositories()
                |> Seq.map (fun repository -> repository.PackageSource.Name, repository)
                |> Map.ofSeq

            let sources =
                sourceProvider.LoadPackageSources()
                |> Seq.filter _.IsEnabled
                |> Seq.map (fun source ->
                    result {
                        let! sourceId = PackageSourceId.create source.Name

                        let model =
                            { Id = sourceId
                              Name =
                                PackageMetadata.text PackageMetadata.limits.Person source.Name
                                |> Option.defaultValue sourceId.Value
                              Location = publicSourceUri root source
                              Availability = PackageSourceAvailability.Available }

                        match Map.tryFind source.Name repositories with
                        | Some repository ->
                            return
                                { Model = model
                                  Configuration = source
                                  Repository = repository }
                        | None ->
                            return!
                                Error(
                                    PackageContractViolation.InvalidValue "packageSourceRepository"
                                )
                    })
                |> Seq.toList

            match
                sources
                |> List.tryPick (function
                    | Error error -> Some error
                    | Ok _ -> None)
            with
            | Some error ->
                Error(
                    NuGetSourceFailures.invalidRequest
                        $"The effective NuGet configuration contains an invalid source ({error})."
                )
            | None ->
                Ok
                    { Sources =
                        sources
                        |> List.choose (function
                            | Ok value -> Some value
                            | _ -> None)
                      Mapping = PackageSourceMapping.GetPackageSourceMapping settings
                      ConfigFiles =
                        settings.GetConfigFilePaths()
                        |> Seq.map Path.GetFullPath
                        |> Seq.distinct
                        |> Seq.sortWith (fun left right ->
                            StringComparer.Ordinal.Compare(left, right))
                        |> Seq.toList }
        with
        | :? OperationCanceledException -> Error(NuGetSourceFailures.cancelled ())
        | _ ->
            Error(
                NuGetSourceFailures.invalidRequest
                    "The effective NuGet configuration could not be read for this target."
            )

    let configuredSources (request: PackageRequest<unit>) =
        async {
            return
                loadCatalog request.Target
                |> Result.map (fun catalog -> catalog.Sources |> List.map _.Model)
        }

    let private mappingSources (catalog: ConfiguredCatalog) (packageId: string) =
        if not catalog.Mapping.IsEnabled then
            catalog.Sources |> List.map (fun source -> source.Model.Id)
        else
            let names = catalog.Mapping.GetConfiguredPackageSources packageId |> Set.ofSeq

            catalog.Sources
            |> List.choose (fun source ->
                if Set.contains source.Configuration.Name names then
                    Some source.Model.Id
                else
                    None)

    let sourceMapping (request: PackageRequest<PackageSourceMappingRequest>) =
        async {
            match loadCatalog request.Target with
            | Error failure -> return Error failure
            | Ok catalog ->
                let packageId = request.Value.Package
                let allowed = mappingSources catalog packageId.Value

                let candidateAllowed =
                    request.Value.CandidateSource
                    |> Option.forall (fun candidate -> List.contains candidate allowed)

                if not catalog.Mapping.IsEnabled then
                    return Ok(PackageSourceMappingPolicy.Allowed allowed)
                elif List.isEmpty allowed || not candidateAllowed then
                    return Ok(PackageSourceMappingPolicy.KnownConflict(packageId, allowed))
                else
                    match request.Value.RestoredTransitives with
                    | None ->
                        return
                            Ok(
                                PackageSourceMappingPolicy.InsufficientRestoredTransitiveEvidence
                                    allowed
                            )
                    | Some transitives ->
                        let conflict =
                            transitives
                            |> List.tryPick (fun transitive ->
                                let transitiveSources = mappingSources catalog transitive.Value

                                if List.isEmpty transitiveSources then
                                    Some(transitive, transitiveSources)
                                else
                                    None)

                        match conflict with
                        | Some(transitive, sources) ->
                            return Ok(PackageSourceMappingPolicy.KnownConflict(transitive, sources))
                        | None -> return Ok(PackageSourceMappingPolicy.Allowed allowed)
        }

    let sourceSelection (catalog: ConfiguredCatalog) (selected: PackageSourceId option) =
        match selected with
        | None -> Ok catalog.Sources
        | Some source ->
            match
                catalog.Sources |> List.tryFind (fun candidate -> candidate.Model.Id = source)
            with
            | Some configured -> Ok [ configured ]
            | None ->
                Error(
                    NuGetSourceFailures.packageFailure
                        PackageFailureKind.NotFound
                        "The selected package source is not enabled for this target."
                        PackageFailureRetry.AfterUserAction
                )
