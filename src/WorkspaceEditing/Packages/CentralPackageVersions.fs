namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex

#nowarn "3261"

open System
open System.IO
open System.Text
open System.Xml.Linq
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.ProjectEvaluation

module internal CentralPackageVersions =
    let private name value = XName.Get value

    let private attribute value (element: XElement) =
        element.Attribute(name value) |> Option.ofObj

    let private textAttribute value element =
        attribute value element |> Option.map _.Value

    let private descendants value (document: XDocument) =
        document.Descendants() |> Seq.filter (fun node -> node.Name.LocalName = value)

    type private Version =
        { Package: string
          Condition: string
          Value: string option }

    let private packageKey (package: string) = package.ToUpperInvariant()

    let private pathComparison workspaceRoot =
        match FileSystemCaseSensitivityDetector.DetectFromExistingPath workspaceRoot with
        | FileSystemCaseSensitivity.Insensitive -> StringComparison.OrdinalIgnoreCase
        | _ -> StringComparison.Ordinal

    let private samePath comparison left right =
        String.Equals(Path.GetFullPath left, Path.GetFullPath right, comparison)

    let rec private hasNestedOwner comparison workspaceRoot owner directory =
        let candidate = Path.Combine(directory, "Directory.Packages.props")

        if File.Exists candidate && not (samePath comparison candidate owner) then
            true
        elif samePath comparison directory workspaceRoot then
            false
        else
            match Directory.GetParent directory with
            | null -> false
            | parent -> hasNestedOwner comparison workspaceRoot owner parent.FullName

    /// Rejects imported membership/ownership and mismatched root ownership before a
    /// package subprocess can mutate a project.  The evaluated snapshot is
    /// authoritative; XML is used only later to render owned replacements.
    let preflight (workspaceRoot: string) (snapshot: ProjectEvaluationSnapshot) =
        let owner = Path.Combine(workspaceRoot, "Directory.Packages.props")
        let comparison = pathComparison workspaceRoot

        let memberships =
            [| for dimension in snapshot.Dimensions do
                   for membership in dimension.PackageMemberships do
                       yield membership |]

        let versions =
            [| for dimension in snapshot.Dimensions do
                   for version in dimension.PackageVersions do
                       yield version |]

        match Path.GetDirectoryName snapshot.ProjectPath.Value |> Option.ofObj with
        | None -> Error "The project path has no containing directory."
        | Some directory when hasNestedOwner comparison workspaceRoot owner directory ->
            Error "A nested Directory.Packages.props owns package versions."
        | Some _ ->
            let importedMembership =
                memberships
                |> Array.tryFind (fun membership ->
                    not (
                        samePath
                            comparison
                            membership.DeclaringPath.Value
                            snapshot.ProjectPath.Value
                    ))

            match importedMembership with
            | Some membership ->
                Error $"Package membership is imported from '{membership.DeclaringPath.Value}'."
            | None ->
                let externalVersion =
                    versions
                    |> Array.tryFind (fun version ->
                        ArtifactFiles.isUnder workspaceRoot version.DeclaringPath.Value
                        && not (samePath comparison version.DeclaringPath.Value owner))

                match externalVersion with
                | Some version ->
                    let path = version.DeclaringPath.Value
                    Error $"Package version ownership is outside the workspace root: '{path}'."
                | None ->
                    let conflicts =
                        versions
                        |> Array.groupBy (fun version -> version.Condition, packageKey version.Id)
                        |> Array.exists (fun (_, values) ->
                            values |> Array.map _.Version |> Array.distinct |> Array.length > 1)

                    if conflicts then
                        Error(
                            "Central package ownership contains conflicting condition/package "
                            + "versions."
                        )
                    else
                        Ok()

    let private packageVersion (reference: XElement) =
        textAttribute "Version" reference
        |> Option.orElseWith (fun () ->
            reference.Elements()
            |> Seq.tryFind (fun element -> element.Name.LocalName = "Version")
            |> Option.map _.Value)

    let private declarations itemName (document: XDocument) =
        [ for item in descendants itemName document do
              match
                  textAttribute "Include" item
                  |> Option.orElseWith (fun () -> textAttribute "Update" item)
              with
              | Some package when not (String.IsNullOrWhiteSpace package) ->
                  let condition =
                      item.Parent
                      |> Option.ofObj
                      |> Option.bind (textAttribute "Condition")
                      |> Option.defaultValue String.Empty

                  yield
                      { Package = package
                        Condition = condition
                        Value = packageVersion item }
              | _ -> () ]

    let private unsafeNestedOwner (root: string) (project: string) =
        let workspaceRoot =
            Path.GetDirectoryName root |> Option.ofObj |> Option.defaultValue root

        let rootIdentity = ArtifactFiles.identity root
        let workspaceIdentity = ArtifactFiles.identity workspaceRoot
        let mutable current = Path.GetDirectoryName project |> Option.ofObj
        let mutable found = false

        while current.IsSome
              && ArtifactFiles.identity current.Value <> workspaceIdentity
              && not found do
            let candidate = Path.Combine(current.Value, "Directory.Packages.props")

            if File.Exists candidate && ArtifactFiles.identity candidate <> rootIdentity then
                found <- true

            current <- Directory.GetParent current.Value |> Option.ofObj |> Option.map _.FullName

        found

    let private ensureCentralManagement (document: XDocument) (lineEnding: string) =
        let group =
            document.Root.Elements(name "PropertyGroup")
            |> Seq.tryFind (fun item ->
                textAttribute "Condition" item
                |> Option.defaultValue String.Empty
                |> String.IsNullOrEmpty)
            |> Option.defaultWith (fun () ->
                let item = XElement(name "PropertyGroup")
                document.Root.Add(XText $"{lineEnding}  ", item, XText lineEnding)
                item)

        let property =
            group.Elements(name "ManagePackageVersionsCentrally")
            |> Seq.tryFind (fun item ->
                textAttribute "Condition" item
                |> Option.defaultValue String.Empty
                |> String.IsNullOrEmpty)

        match property with
        | Some property -> property.Value <- "true"
        | None ->
            group.Add(
                XText $"{lineEnding}    ",
                XElement(name "ManagePackageVersionsCentrally", "true"),
                XText $"{lineEnding}  "
            )

    /// Converts only project-owned declarations and merges matching conditional entries into the
    /// root owner. Imported membership or a nested owner is refused before mutation rather than
    /// guessed.
    let normalize (workspaceRoot: string) (project: string) =
        try
            let owner = Path.Combine(workspaceRoot, "Directory.Packages.props")

            if unsafeNestedOwner owner project then
                let message =
                    "A nested Directory.Packages.props owns package versions; "
                    + "root consolidation is unsafe."

                Error message
            else
                let projectDocument, projectEncoding, projectPreamble, projectLineEnding =
                    ProjectEditing.readDocument project

                let memberships = declarations "PackageReference" projectDocument

                if List.isEmpty memberships then
                    Ok []
                else
                    let ownerDocument, ownerEncoding, ownerPreamble, ownerLineEnding =
                        if File.Exists owner then
                            ProjectEditing.readDocument owner
                        else
                            XDocument(
                                XElement(
                                    name "Project",
                                    XElement(
                                        name "PropertyGroup",
                                        XElement(name "ManagePackageVersionsCentrally", "true")
                                    )
                                )
                            ),
                            Encoding.UTF8,
                            false,
                            Environment.NewLine

                    let ownerVersions = declarations "PackageVersion" ownerDocument
                    ensureCentralManagement ownerDocument ownerLineEnding

                    let resolve membership =
                        match membership.Value with
                        | Some value -> Ok value
                        | None ->
                            let exact =
                                ownerVersions
                                |> List.filter (fun version ->
                                    String.Equals(
                                        version.Package,
                                        membership.Package,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                    && version.Condition = membership.Condition)
                                |> List.choose _.Value
                                |> List.distinct

                            match exact with
                            | [ value ] -> Ok value
                            | [] ->
                                Error(
                                    $"Package '{membership.Package}' has no version for its "
                                    + "exact ItemGroup condition."
                                )
                            | _ ->
                                Error(
                                    $"Package '{membership.Package}' has conflicting versions "
                                    + "for its exact ItemGroup condition."
                                )

                    let proposedResult =
                        memberships
                        |> List.fold
                            (fun result membership ->
                                match result, resolve membership with
                                | Ok values, Ok value ->
                                    Ok(
                                        ((membership.Condition, packageKey membership.Package),
                                         membership.Package,
                                         value)
                                        :: values
                                    )
                                | Error error, _
                                | _, Error error -> Error error)
                            (Ok [])

                    match proposedResult with
                    | Error message -> Error message
                    | Ok proposedValues ->
                        let proposedGroups = proposedValues |> List.groupBy (fun (key, _, _) -> key)

                        if
                            proposedGroups
                            |> List.exists (fun (_, values) ->
                                values
                                |> List.map (fun (_, _, value) -> value)
                                |> List.distinct
                                |> List.length
                                |> (<>) 1)
                        then
                            let message =
                                "The same package and ItemGroup condition resolve to "
                                + "conflicting versions."

                            Error message
                        else
                            let proposed =
                                proposedGroups
                                |> List.map (fun (key, values) ->
                                    let _, package, value = List.head values
                                    key, package, value)

                            let existingGroups =
                                ownerVersions
                                |> List.choose (fun version ->
                                    version.Value
                                    |> Option.map (fun value ->
                                        (version.Condition, packageKey version.Package), value))
                                |> List.groupBy fst

                            let conflictingExisting =
                                existingGroups
                                |> List.exists (fun (_, values) ->
                                    values |> List.map snd |> List.distinct |> List.length > 1)

                            let existing =
                                existingGroups
                                |> List.map (fun (key, values) -> key, List.head values |> snd)
                                |> Map.ofList

                            if
                                conflictingExisting
                                || proposed
                                   |> List.exists (fun (key, _, value) ->
                                       existing |> Map.tryFind key |> Option.exists ((<>) value))
                            then
                                let message =
                                    "The root central package file has a conflicting "
                                    + "condition/package version."

                                Error message
                            else
                                for reference in descendants "PackageReference" projectDocument do
                                    attribute "Version" reference |> Option.iter _.Remove()

                                    reference.Elements()
                                    |> Seq.filter (fun element ->
                                        element.Name.LocalName = "Version")
                                    |> Seq.toList
                                    |> List.iter _.Remove()

                                for (condition, _), package, value in proposed do
                                    if
                                        not (existing.ContainsKey(condition, packageKey package))
                                    then
                                        let group =
                                            ownerDocument.Root.Elements(name "ItemGroup")
                                            |> Seq.tryFind (fun item ->
                                                textAttribute "Condition" item
                                                |> Option.defaultValue String.Empty = condition)
                                            |> Option.defaultWith (fun () ->
                                                let item = XElement(name "ItemGroup")

                                                if not (String.IsNullOrEmpty condition) then
                                                    item.SetAttributeValue(
                                                        name "Condition",
                                                        condition
                                                    )

                                                ownerDocument.Root.Add(
                                                    XText $"{ownerLineEnding}  ",
                                                    item,
                                                    XText ownerLineEnding
                                                )

                                                item)

                                        group.Add(
                                            XText $"{ownerLineEnding}    ",
                                            XElement(
                                                name "PackageVersion",
                                                XAttribute(name "Include", package),
                                                XAttribute(name "Version", value)
                                            ),
                                            XText $"{ownerLineEnding}  "
                                        )

                                Ok
                                    [ project,
                                      ProjectEditing.saveDocument
                                          projectDocument
                                          projectEncoding
                                          projectPreamble
                                          projectLineEnding
                                      owner,
                                      ProjectEditing.saveDocument
                                          ownerDocument
                                          ownerEncoding
                                          ownerPreamble
                                          ownerLineEnding ]
        with
        | :? IOException as error -> Error error.Message
        | :? Xml.XmlException as error -> Error error.Message
