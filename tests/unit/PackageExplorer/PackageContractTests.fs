namespace Dotnet.WorkspaceExplorer.PackageExplorer.UnitTests

open System
open Dotnet.WorkspaceExplorer.Packages
open FsUnit.Xunit
open Xunit

module private PackageContractScenario =
    let packageId value =
        PackageId.create value |> Result.defaultWith (failwithf "%A")

    let version value =
        NuGetVersion.create value |> Result.defaultWith (failwithf "%A")

    let project value =
        PackageProjectId.create value |> Result.defaultWith (failwithf "%A")

    let violation result =
        match result with
        | Error violation -> violation
        | Ok _ -> failwith "Expected a package contract violation."

[<Sealed>]
type PackageContractTests() =
    [<Fact>]
    member _.``package value contracts reject blank text and empty operation identifiers``() =
        PackageId.create " "
        |> PackageContractScenario.violation
        |> should equal (PackageContractViolation.MissingValue "packageId")

        NuGetVersion.create "1.0\n2.0"
        |> PackageContractScenario.violation
        |> should equal (PackageContractViolation.InvalidValue "version")

        PackageOperationId.create Guid.Empty
        |> PackageContractScenario.violation
        |> should equal (PackageContractViolation.InvalidValue "operationId")

    [<Fact>]
    member _.``package progress contracts reject negative overflowing and zero-total progress``() =
        let operation = PackageOperationId.newId ()

        PackageProgress.determinate operation PackageOperationStage.Applying -1 10
        |> PackageContractScenario.violation
        |> should equal (PackageContractViolation.OutOfRange "progress")

        PackageProgress.determinate operation PackageOperationStage.Applying 11 10
        |> PackageContractScenario.violation
        |> should equal (PackageContractViolation.OutOfRange "progress")

        PackageProgress.determinate operation PackageOperationStage.Applying 0 0
        |> PackageContractScenario.violation
        |> should equal (PackageContractViolation.OutOfRange "progress")

        let valid =
            PackageProgress.determinate operation PackageOperationStage.Applying 4 10
            |> Result.defaultWith (failwithf "%A")

        PackageProgress.completed valid |> should equal (Some(4, 10))

    [<Fact>]
    member _.``package operation contracts express target scope and requested version without flags``
        ()
        =
        let package = PackageContractScenario.packageId "Example.Package"
        let version = PackageContractScenario.version "2.0.0"
        let project = PackageContractScenario.project "src/Example.csproj"
        let target = PackageTargetScope.Project project

        let request =
            { Operation = RequestedPackageOperation.UpdateVersion(package, version)
              Targets = NonEmptyList.singleton target
              BrowseSource = None
              Precondition =
                { WorkspaceRevision = "revision"
                  FileFingerprints = Map [ "src/Example.csproj", "hash" ] } }

        request.Operation
        |> should equal (RequestedPackageOperation.UpdateVersion(package, version))

        request.Targets |> NonEmptyList.toList |> should equal [ target ]

    [<Fact>]
    member _.``package preview contracts require a revision fingerprints changes and owner files``
        ()
        =
        let package = PackageContractScenario.packageId "Example.Package"
        let version = PackageContractScenario.version "2.0.0"
        let project = PackageContractScenario.project "src/Example.csproj"
        let target = PackageTargetScope.Project project
        let operation = RequestedPackageOperation.InstallVersion(package, version)

        let impact =
            { Metadata = PackageMetadataImpact.Unknown
              SourceMapping = PackageSourceMappingImpact.ApplyAllowed []
              Restore =
                PackageRestoreImpact.RequiredWithUnknownOutcome PackageGraphFreshness.Current }

        let targetPreview =
            PackageTargetPreview.create
                target
                (PackageTargetChange.Install(None, ProposedPackageState.Direct version))
                (NonEmptyList.singleton "src/Example.csproj")
                PackageGraphFreshness.Current
                impact
            |> Result.defaultWith (failwithf "%A")

        let targets = NonEmptyList.singleton targetPreview

        let owners = NonEmptyList.singleton "src/Example.csproj"

        PackagePreview.create operation targets owners "" (Map [ "src/Example.csproj", "hash" ])
        |> PackageContractScenario.violation
        |> should equal (PackageContractViolation.MissingValue "workspaceRevision")

        PackagePreview.create operation targets owners "revision" Map.empty
        |> PackageContractScenario.violation
        |> should equal (PackageContractViolation.InvalidValue "fileFingerprints")

        PackagePreview.create
            (RequestedPackageOperation.Uninstall package)
            targets
            owners
            "revision"
            (Map [ "src/Example.csproj", "hash" ])
        |> PackageContractScenario.violation
        |> should equal (PackageContractViolation.InvalidValue "targetChanges")

    [<Fact>]
    member _.``package failure contracts derive stable codes from one failure classification``() =
        let failure =
            PackageFailure.create
                PackageFailureKind.AuthenticationRequired
                "The configured source requires authentication."
                PackageFailureRetry.AfterUserAction
            |> Result.defaultWith (failwithf "%A")

        PackageFailure.code failure
        |> should equal "DWE-PACKAGE-AUTHENTICATION-REQUIRED"

        PackageFailure.kind failure
        |> should equal PackageFailureKind.AuthenticationRequired
