namespace Dotnet.CLI.Plus

#nowarn "3261"
#nowarn "3511"

open System
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution

module internal Verify =
    let private openSolution target cancellationToken =
        task {
            let! outcome =
                SolutionStore.OpenAsync(
                    target |> Option.defaultValue (Directory.GetCurrentDirectory()),
                    cancellationToken
                )

            return
                match outcome with
                | Success workspace -> Ok workspace
                | Failure failure -> Error failure
        }

    let prepareSolution
        (target: string option)
        (operation: SolutionOperation)
        (operands: string list)
        cancellationToken
        =
        task {
            if
                (operation = Add || operation = Remove)
                && (List.isEmpty operands
                    || operands
                       |> List.exists (fun operand ->
                           operand.IndexOfAny [| '*'; '?' |] >= 0
                           && List.isEmpty (Paths.expandSolutionOperand operand)))
            then
                return
                    Error(
                        BrokerFailure.invalid
                            "Solution add/remove requires one or more matching project operands."
                    )
            else
                match target with
                | Some path when path.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase) ->
                    return
                        Error(
                            BrokerFailure.unsupported
                                ".slnf workspaces are read-only and cannot be mutated."
                        )
                | _ ->
                    let! workspace = openSolution target cancellationToken

                    return
                        match workspace with
                        | Error failure -> Error failure
                        | Ok workspace when workspace.WorkspaceDescriptor.IsReadOnly ->
                            Error(
                                BrokerFailure.unsupported
                                    ".slnf workspaces are read-only and cannot be mutated."
                            )
                        | Ok workspace -> Ok workspace
        }

    let private solutionProjects (workspace: SolutionWorkspace) =
        workspace.RootProjection.Projects
        |> Seq.map (fun project -> project.Node.Name, project.Path.AbsolutePath.Value)
        |> Seq.toList

    let private requestedSolutionOperands operands =
        let expanded = operands |> List.collect Paths.expandSolutionOperand

        if List.isEmpty operands || List.isEmpty expanded then
            Error "Solution add/remove requires at least one verifiable project operand."
        else
            Ok expanded

    let verifySolution target operation operands cancellationToken =
        task {
            let! opened = openSolution target cancellationToken

            match opened with
            | Error failure -> return Error failure
            | Ok workspace ->
                let pathComparer =
                    match
                        HostFileSystemCaseDetector.DetectFromExistingPath
                            workspace.BackingPath.Value
                    with
                    | HostFileSystemCaseSemantics.Insensitive -> StringComparer.OrdinalIgnoreCase
                    | _ -> StringComparer.Ordinal

                match operation with
                | Some Add
                | Some Remove ->
                    match requestedSolutionOperands operands with
                    | Error message -> return Error(BrokerFailure.invalid message)
                    | Ok requested ->
                        let projects = solutionProjects workspace

                        let matches operand =
                            projects
                            |> List.exists (fun (name, path) ->
                                String.Equals(name, operand, StringComparison.OrdinalIgnoreCase)
                                || pathComparer.Equals(path, Path.GetFullPath operand))

                        let correct =
                            match operation with
                            | Some Add -> requested |> List.forall matches
                            | _ -> requested |> List.forall (matches >> not)

                        if correct then
                            return Ok(Some workspace.WorkspaceDescriptor.WorkspaceRevision)
                        else
                            return
                                Error(
                                    BrokerFailure.verification (
                                        "The refreshed solution does not contain the requested "
                                        + "final project state."
                                    )
                                )
                | Some Migrate ->
                    let migrated = Path.ChangeExtension(workspace.BackingPath.Value, ".slnx")

                    if File.Exists migrated then
                        return Ok(Some workspace.WorkspaceDescriptor.WorkspaceRevision)
                    else
                        return
                            Error(
                                BrokerFailure.verification
                                    "The migrated .slnx file was not created."
                            )
                | _ -> return Ok(Some workspace.WorkspaceDescriptor.WorkspaceRevision)
        }

    let private descendants name (document: XDocument) =
        document.Descendants()
        |> Seq.filter (fun element -> element.Name.LocalName = name)

    let private attribute name (element: XElement) =
        element.Attribute(XName.Get name) |> Option.ofObj |> Option.map _.Value

    let private itemGroupCondition (element: XElement) =
        element.Parent
        |> Option.ofObj
        |> Option.bind (attribute "Condition")
        |> Option.defaultValue String.Empty

    let private conditionAppliesToFramework framework condition =
        match framework with
        | None -> true
        | Some _ when String.IsNullOrWhiteSpace condition -> true
        | Some expected ->
            let compact = Regex.Replace(condition, "\\s+", String.Empty)

            [ $"'$(TargetFramework)'=='{expected}'"
              $"\"$(TargetFramework)\"==\"{expected}\""
              $"$(TargetFramework)=='{expected}'"
              $"$(TargetFramework)==\"{expected}\"" ]
            |> List.exists (fun candidate ->
                String.Equals(compact, candidate, StringComparison.OrdinalIgnoreCase))

    let packageSubject (value: string) =
        let index = value.LastIndexOf '@'

        if index > 0 then
            value.Substring(0, index), Some(value.Substring(index + 1))
        else
            value, None

    let private centralVersion (project: string) (id: string) condition =
        let rec find directory =
            let candidate = Path.Combine(directory, "Directory.Packages.props")

            if File.Exists candidate then
                let document = XDocument.Load candidate

                descendants "PackageVersion" document
                |> Seq.tryFind (fun element ->
                    attribute "Include" element
                    |> Option.orElseWith (fun () -> attribute "Update" element)
                    |> Option.exists (fun value ->
                        String.Equals(value, id, StringComparison.OrdinalIgnoreCase))
                    && String.Equals(
                        itemGroupCondition element,
                        condition,
                        StringComparison.Ordinal
                    ))
                |> Option.bind (fun element ->
                    attribute "Version" element
                    |> Option.orElseWith (fun () ->
                        element.Elements()
                        |> Seq.tryFind (fun child -> child.Name.LocalName = "Version")
                        |> Option.map _.Value))
            else
                match Directory.GetParent directory with
                | null -> None
                | parent -> find parent.FullName

        Path.GetDirectoryName project |> Option.ofObj |> Option.bind find

    let verifyPackage operation (project: string) framework operands =
        match operands with
        | [] -> Error(BrokerFailure.invalid "Package mutations require a package ID.")
        | subjects ->
            let document = XDocument.Load project
            let references = descendants "PackageReference" document |> Seq.toList

            let present subject =
                let id, version = packageSubject subject

                references
                |> List.exists (fun reference ->
                    let condition = itemGroupCondition reference

                    let matchesId =
                        attribute "Include" reference
                        |> Option.orElseWith (fun () -> attribute "Update" reference)
                        |> Option.exists (fun actual ->
                            String.Equals(actual, id, StringComparison.OrdinalIgnoreCase))

                    let actualVersion =
                        attribute "Version" reference
                        |> Option.orElseWith (fun () ->
                            reference.Elements()
                            |> Seq.tryFind (fun child -> child.Name.LocalName = "Version")
                            |> Option.map _.Value)

                    let effectiveVersion =
                        actualVersion
                        |> Option.orElseWith (fun () -> centralVersion project id condition)

                    matchesId
                    && conditionAppliesToFramework framework condition
                    && version |> Option.forall (fun expected -> effectiveVersion = Some expected))

            let correct =
                match operation with
                | PackageAdd
                | PackageUpdate -> subjects |> List.forall present
                | PackageRemove -> subjects |> List.forall (present >> not)
                | _ -> true

            if correct then
                Ok None
            else
                Error(
                    BrokerFailure.verification
                        "The refreshed project does not contain the requested package state."
                )

    let verifyReferences operation (project: string) framework operands =
        if List.isEmpty operands then
            Error(BrokerFailure.invalid "Reference mutations require one or more project operands.")
        else
            let projectDirectory =
                Path.GetDirectoryName project
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let document = XDocument.Load project

            let comparer =
                match HostFileSystemCaseDetector.DetectFromExistingPath project with
                | HostFileSystemCaseSemantics.Insensitive -> StringComparer.OrdinalIgnoreCase
                | _ -> StringComparer.Ordinal

            let references =
                descendants "ProjectReference" document
                |> Seq.choose (fun reference ->
                    attribute "Include" reference
                    |> Option.map (fun value ->
                        Path.GetFullPath(value, projectDirectory), itemGroupCondition reference))
                |> Seq.filter (fun (_, condition) ->
                    conditionAppliesToFramework framework condition)
                |> Seq.map fst
                |> Seq.toList

            let requested =
                operands |> List.map (fun value -> Path.GetFullPath(value, projectDirectory))

            let correct =
                match operation with
                | ReferenceAdd ->
                    requested
                    |> List.forall (fun value ->
                        references
                        |> List.exists (fun reference -> comparer.Equals(reference, value)))
                | ReferenceRemove ->
                    requested
                    |> List.forall (fun value ->
                        references
                        |> List.exists (fun reference -> comparer.Equals(reference, value))
                        |> not)
                | _ -> true

            if correct then
                Ok None
            else
                Error(
                    BrokerFailure.verification
                        "The refreshed project does not contain the requested reference state."
                )

    let snapshot (directory: string) =
        if Directory.Exists directory then
            Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            |> Seq.map (fun path ->
                let info = FileInfo path
                path, (info.Length, info.LastWriteTimeUtc.Ticks))
            |> Map.ofSeq
        else
            Map.empty

    let verifyNew (output: string) before =
        let after = snapshot output

        if after <> before then
            Ok None
        else
            Error(
                BrokerFailure.verification
                    "The template command did not create a verifiable output state."
            )
