namespace Dotnet.WorkspaceExplorer.Packages

open System

[<RequireQualifiedAccess>]
type PackageContractViolation =
    | MissingValue of field: string
    | InvalidValue of field: string
    | OutOfRange of field: string

module private PackageValue =
    let text field (value: string) =
        if String.IsNullOrWhiteSpace value then
            Error(PackageContractViolation.MissingValue field)
        elif value |> Seq.exists Char.IsControl then
            Error(PackageContractViolation.InvalidValue field)
        else
            Ok value

[<Struct>]
type PackageId =
    private
    | PackageId of string

    member this.Value =
        let (PackageId value) = this
        value

[<RequireQualifiedAccess>]
module PackageId =
    let create value =
        PackageValue.text "packageId" value |> Result.map PackageId

[<Struct>]
type NuGetVersion =
    private
    | NuGetVersion of string

    member this.Value =
        let (NuGetVersion value) = this
        value

[<RequireQualifiedAccess>]
module NuGetVersion =
    let create value =
        PackageValue.text "version" value |> Result.map NuGetVersion

[<Struct>]
type NuGetVersionRange =
    private
    | NuGetVersionRange of string

    member this.Value =
        let (NuGetVersionRange value) = this
        value

[<RequireQualifiedAccess>]
module NuGetVersionRange =
    let create value =
        PackageValue.text "versionRange" value |> Result.map NuGetVersionRange

[<Struct>]
type PackageSourceId =
    private
    | PackageSourceId of string

    member this.Value =
        let (PackageSourceId value) = this
        value

[<RequireQualifiedAccess>]
module PackageSourceId =
    let create value =
        PackageValue.text "sourceId" value |> Result.map PackageSourceId

[<Struct>]
type PackageProjectId =
    private
    | PackageProjectId of string

    member this.Value =
        let (PackageProjectId value) = this
        value

[<RequireQualifiedAccess>]
module PackageProjectId =
    let create value =
        PackageValue.text "projectId" value |> Result.map PackageProjectId

[<Struct>]
type TargetFramework =
    private
    | TargetFramework of string

    member this.Value =
        let (TargetFramework value) = this
        value

[<RequireQualifiedAccess>]
module TargetFramework =
    let create value =
        PackageValue.text "targetFramework" value |> Result.map TargetFramework

[<Struct>]
type PackageRequestId =
    private
    | PackageRequestId of Guid

    member this.Value =
        let (PackageRequestId value) = this
        value

[<RequireQualifiedAccess>]
module PackageRequestId =
    let create value =
        if value = Guid.Empty then
            Error(PackageContractViolation.InvalidValue "requestId")
        else
            Ok(PackageRequestId value)

    let newId () = PackageRequestId(Guid.NewGuid())

[<Struct>]
type PackageOperationId =
    private
    | PackageOperationId of Guid

    member this.Value =
        let (PackageOperationId value) = this
        value

[<RequireQualifiedAccess>]
module PackageOperationId =
    let create value =
        if value = Guid.Empty then
            Error(PackageContractViolation.InvalidValue "operationId")
        else
            Ok(PackageOperationId value)

    let newId () = PackageOperationId(Guid.NewGuid())

type NonEmptyList<'value> =
    private
        { Head: 'value
          Tail: 'value list }

[<RequireQualifiedAccess>]
module NonEmptyList =
    let create head tail = { Head = head; Tail = tail }

    let tryCreate values =
        match values with
        | head :: tail -> Some(create head tail)
        | [] -> None

    let singleton value = create value []
    let toList values = values.Head :: values.Tail

    let map mapping values =
        create (mapping values.Head) (values.Tail |> List.map mapping)
