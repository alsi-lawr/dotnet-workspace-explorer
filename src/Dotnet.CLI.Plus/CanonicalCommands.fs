namespace Dotnet.CLI.Plus

#nowarn "3261"

open System
open System.Collections.Immutable
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open Dotnet.CLI.Plus.Core
open Dotnet.CLI.Plus.Solution

/// Thin typed surface over the existing canonical dotnet broker.  The broker remains
/// the subprocess authority; this module only maps node-oriented RPC arguments to argv.
module internal CanonicalCommands =
    let private parameter id parameterType required displayName =
        CommandParameterDescriptor.Create(CommandParameterId.Create id, parameterType, required, displayName)

    let private projectCommand id displayName access parameters =
        CommandDescriptor.Create(CommandId.Create id, displayName, access, parameters, [ WorkspaceNodeKind.Project ])

    let private templateCommand id displayName access parameters =
        CommandDescriptor.Create(
            CommandId.Create id,
            displayName,
            access,
            parameters,
            [ WorkspaceNodeKind.Workspace; WorkspaceNodeKind.SolutionFolder ]
        )

    let private extra =
        parameter "arguments" CommandParameterType.TextArray false "Additional canonical arguments"

    let private framework =
        parameter "framework" CommandParameterType.Text false "Target framework"

    let private noRestore =
        parameter "noRestore" CommandParameterType.Boolean false "Do not restore"

    let private path =
        parameter "path" CommandParameterType.Path true "Referenced project"

    let private packageId = parameter "id" CommandParameterType.Text true "Package ID"

    let private version =
        parameter "version" CommandParameterType.Text false "Package version"

    let private template =
        parameter "template" CommandParameterType.Text true "Template short name"

    let private output =
        parameter "output" CommandParameterType.Path false "Output directory"

    let private dryRun =
        parameter "dryRun" CommandParameterType.Boolean false "Preview without creating files"

    let projectDescriptors =
        ImmutableArray.CreateRange(
            [ projectCommand "reference.list" "List project references" CommandAccess.Read [ extra ]
              projectCommand "reference.add" "Add project reference" CommandAccess.Write [ path; framework; extra ]
              projectCommand
                  "reference.remove"
                  "Remove project reference"
                  CommandAccess.Write
                  [ path; framework; extra ]
              projectCommand "package.list" "List packages" CommandAccess.Read [ noRestore; framework; extra ]
              projectCommand
                  "package.add"
                  "Add package"
                  CommandAccess.Write
                  [ packageId; version; noRestore; framework; extra ]
              projectCommand "package.remove" "Remove package" CommandAccess.Write [ packageId; extra ]
              projectCommand
                  "package.update"
                  "Update package"
                  CommandAccess.Write
                  [ parameter "id" CommandParameterType.Text false "Package ID"; version; extra ] ]
        )

    let templateDescriptors =
        ImmutableArray.CreateRange(
            [ templateCommand "template.list" "List templates" CommandAccess.Read [ extra ]
              templateCommand "template.show" "Show template details" CommandAccess.Read [ template; extra ]
              templateCommand
                  "template.create"
                  "Create template"
                  CommandAccess.Write
                  [ template; output; dryRun; extra ] ]
        )

    let tryDescribe id =
        Seq.append projectDescriptors templateDescriptors
        |> Seq.tryFind (fun descriptor -> descriptor.CommandId = id)

    let discover (workspace: SolutionWorkspace) target =
        if workspace.WorkspaceDescriptor.IsReadOnly then
            ImmutableArray<CommandDescriptor>.Empty
        else
            match target with
            | Some target when
                workspace.RootProjection.Projects
                |> Seq.exists (fun project -> project.Node.NodeId = target)
                ->
                projectDescriptors
            | None -> templateDescriptors
            | Some target when
                workspace.RootProjection.Folders
                |> Seq.exists (fun folder -> folder.Node.NodeId = target)
                ->
                templateDescriptors
            | _ -> ImmutableArray<CommandDescriptor>.Empty

    let private argument id (arguments: CommandArguments) =
        arguments.Values
        |> Seq.tryPick (fun candidate ->
            if candidate.ParameterId.Value = id then
                Some candidate.Value
            else
                None)

    let private textArray arguments =
        match argument "arguments" arguments with
        | None -> Ok []
        | Some(TextArray values) when values |> Seq.forall (String.IsNullOrWhiteSpace >> not) ->
            Ok(values |> Seq.toList)
        | Some(TextArray _) -> Error "Canonical arguments must not contain empty values."
        | _ -> Error "Canonical arguments must be a text array."

    let private optionalText id arguments =
        match argument id arguments with
        | None -> Ok None
        | Some(Text value) when not (String.IsNullOrWhiteSpace value) -> Ok(Some value)
        | _ -> Error $"'{id}' must be non-empty text."

    let private optionalBoolean id arguments =
        match argument id arguments with
        | None -> Ok false
        | Some(Boolean value) -> Ok value
        | _ -> Error $"'{id}' must be a boolean."

    let private requiredText id arguments =
        match argument id arguments with
        | Some(Text value) when not (String.IsNullOrWhiteSpace value) -> Ok value
        | _ -> Error $"'{id}' is required."

    let private optionalPath id arguments =
        match argument id arguments with
        | None -> Ok None
        | Some(Path value) -> Ok(Some value.Value)
        | _ -> Error $"'{id}' must be a path."

    let private requiredPath id arguments =
        match optionalPath id arguments with
        | Ok(Some value) -> Ok value
        | Ok None -> Error $"'{id}' is required."
        | Error error -> Error error

    let private projectPath (workspace: SolutionWorkspace) target =
        match
            target
            |> Option.bind (fun id ->
                workspace.RootProjection.Projects
                |> Seq.tryFind (fun project -> project.Node.NodeId = id)
                |> Option.map (fun project -> project.Path.AbsolutePath.Value))
        with
        | Some path -> Ok path
        | None -> Error "A project target is required."

    let private templateDirectory (workspace: SolutionWorkspace) target =
        let root =
            Path.GetDirectoryName workspace.BackingPath.Value
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        match target with
        | None -> Ok root
        | Some id ->
            match
                workspace.RootProjection.Folders
                |> Seq.tryFind (fun folder -> folder.Node.NodeId = id)
            with
            | Some folder ->
                let segments =
                    folder.Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)

                Ok(Array.fold (fun (directory: string) segment -> Path.Combine(directory, segment)) root segments)
            | None -> Error "The template target must be the workspace root or a solution folder."

    let private operationName commandId =
        match commandId with
        | "reference.list" -> Ok("reference", "list")
        | "reference.add" -> Ok("reference", "add")
        | "reference.remove" -> Ok("reference", "remove")
        | "package.list" -> Ok("package", "list")
        | "package.add" -> Ok("package", "add")
        | "package.remove" -> Ok("package", "remove")
        | "package.update" -> Ok("package", "update")
        | "template.list" -> Ok("new", "list")
        | "template.show" -> Ok("new", "details")
        | "template.create" -> Ok("new", "create")
        | _ -> Error "The canonical command is not supported."

    let argv (workspace: SolutionWorkspace) (request: CommandMutationRequest) =
        match operationName request.CommandId.Value, textArray request.Arguments with
        | Error error, _
        | _, Error error -> Error error
        | Ok(command, verb), Ok extraArguments ->
            match command with
            | "reference" ->
                match
                    projectPath workspace request.TargetId,
                    (if verb = "list" then
                         Ok None
                     else
                         optionalText "framework" request.Arguments)
                with
                | Error error, _ -> Error error
                | _, Error error -> Error error
                | Ok project, Ok frameworkValue ->
                    match
                        (if verb = "list" then
                             Ok []
                         else
                             requiredPath "path" request.Arguments |> Result.map List.singleton)
                    with
                    | Error error -> Error error
                    | Ok reference ->
                        let options =
                            [ yield command
                              yield verb
                              yield "--project"
                              yield project
                              match frameworkValue with
                              | Some value ->
                                  yield "--framework"
                                  yield value
                              | None -> ()
                              yield! reference
                              yield! extraArguments ]

                        Ok options
            | "package" ->
                let id =
                    if verb = "list" || verb = "update" then
                        optionalText "id" request.Arguments
                    else
                        requiredText "id" request.Arguments |> Result.map Some

                let frameworkValue =
                    if verb = "remove" || verb = "update" then
                        Ok None
                    else
                        optionalText "framework" request.Arguments

                let noRestoreValue =
                    if verb = "remove" || verb = "update" then
                        Ok false
                    else
                        optionalBoolean "noRestore" request.Arguments

                match
                    projectPath workspace request.TargetId,
                    id,
                    optionalText "version" request.Arguments,
                    frameworkValue,
                    noRestoreValue
                with
                | Error error, _, _, _, _
                | _, Error error, _, _, _
                | _, _, Error error, _, _
                | _, _, _, Error error, _
                | _, _, _, _, Error error -> Error error
                | Ok project, Ok id, Ok versionValue, Ok frameworkValue, Ok noRestoreValue when
                    versionValue.IsSome && id.IsNone
                    ->
                    Error "A package version requires a package ID."
                | Ok project, Ok id, Ok versionValue, Ok frameworkValue, Ok noRestoreValue ->
                    let options =
                        [ yield command
                          yield verb
                          yield "--project"
                          yield project
                          if noRestoreValue then
                              yield "--no-restore"
                          match frameworkValue with
                          | Some value ->
                              yield "--framework"
                              yield value
                          | None -> ()
                          match id, versionValue with
                          | Some value, Some version when verb = "update" -> yield $"{value}@{version}"
                          | Some value, Some version ->
                              yield value
                              yield "--version"
                              yield version
                          | Some value, None -> yield value
                          | None, None -> ()
                          | None, Some _ -> ()
                          yield! extraArguments ]

                    Ok options
            | "new" ->
                match templateDirectory workspace request.TargetId with
                | Error error -> Error error
                | Ok workspaceRoot ->
                    match
                        if verb = "list" then
                            Ok(None, None, false)
                        elif verb = "details" then
                            requiredText "template" request.Arguments
                            |> Result.map (fun value -> Some value, None, false)
                        else
                            match
                                requiredText "template" request.Arguments,
                                optionalPath "output" request.Arguments,
                                optionalBoolean "dryRun" request.Arguments
                            with
                            | Ok value, Ok destination, Ok preview -> Ok(Some value, destination, preview)
                            | Error error, _, _
                            | _, Error error, _
                            | _, _, Error error -> Error error
                    with
                    | Error error -> Error error
                    | Ok(templateValue, destination, preview) ->
                        Ok
                            [ yield "new"
                              if verb = "list" then
                                  yield "list"
                              if verb = "details" then
                                  yield "details"
                              match templateValue with
                              | Some value -> yield value
                              | None -> ()
                              yield! extraArguments
                              if verb = "create" then
                                  yield "--output"
                                  yield destination |> Option.defaultValue workspaceRoot

                                  if preview then
                                      yield "--dry-run" ]
            | _ -> Error "The canonical command is not supported."

    let isMutation commandId =
        commandId = "reference.add"
        || commandId = "reference.remove"
        || commandId = "package.add"
        || commandId = "package.remove"
        || commandId = "package.update"
        || commandId = "template.create"

    let isPackageMutation commandId =
        commandId = "package.add"
        || commandId = "package.remove"
        || commandId = "package.update"

module internal CentralPackageManagement =
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
          Value: string }

    let private packageVersion (reference: XElement) =
        textAttribute "Version" reference
        |> Option.orElseWith (fun () ->
            reference.Elements()
            |> Seq.tryFind (fun element -> element.Name.LocalName = "Version")
            |> Option.map _.Value)

    let private versions (project: string) =
        let document = XDocument.Load(project, LoadOptions.PreserveWhitespace)

        document,
        [ for reference in descendants "PackageReference" document do
              match
                  textAttribute "Include" reference
                  |> Option.orElseWith (fun () -> textAttribute "Update" reference),
                  packageVersion reference
              with
              | Some package, Some version when
                  not (String.IsNullOrWhiteSpace package || String.IsNullOrWhiteSpace version)
                  ->
                  let condition =
                      reference.Parent
                      |> Option.ofObj
                      |> Option.bind (textAttribute "Condition")
                      |> Option.defaultValue String.Empty

                  yield
                      { Package = package
                        Condition = condition
                        Value = version }
              | _ -> () ]

    let private unsafeNestedOwner (root: string) (project: string) =
        let mutable current = Path.GetDirectoryName project |> Option.ofObj
        let mutable found = false

        while current.IsSome && not found do
            let candidate = Path.Combine(current.Value, "Directory.Packages.props")

            if
                File.Exists candidate
                && not (String.Equals(candidate, root, StringComparison.Ordinal))
            then
                found <- true

            current <- Directory.GetParent(current.Value) |> Option.ofObj |> Option.map _.FullName

        found

    /// Converts only project-owned declarations and merges matching conditional entries into the root owner.
    /// Imported membership or a nested owner is refused before mutation rather than guessed.
    let normalize (workspaceRoot: string) (project: string) =
        try
            let owner = Path.Combine(workspaceRoot, "Directory.Packages.props")

            if unsafeNestedOwner owner project then
                Error "A nested Directory.Packages.props owns package versions; root consolidation is unsafe."
            else
                let projectDocument, discovered = versions project

                if List.isEmpty discovered then
                    Ok []
                else
                    let grouped =
                        discovered
                        |> Seq.groupBy (fun version -> version.Condition, version.Package)
                        |> Seq.toList

                    if
                        grouped
                        |> Seq.exists (fun (_, values) -> values |> Seq.map _.Value |> Seq.distinct |> Seq.length <> 1)
                    then
                        Error "The same package and ItemGroup condition resolve to conflicting versions."
                    else
                        let ownerDocument =
                            if File.Exists owner then
                                XDocument.Load(owner, LoadOptions.PreserveWhitespace)
                            else
                                XDocument(XElement(name "Project"))

                        let existing =
                            [ for entry in descendants "PackageVersion" ownerDocument do
                                  match
                                      textAttribute "Include" entry
                                      |> Option.orElseWith (fun () -> textAttribute "Update" entry),
                                      packageVersion entry
                                  with
                                  | Some package, Some value ->
                                      let condition =
                                          entry.Parent
                                          |> Option.ofObj
                                          |> Option.bind (textAttribute "Condition")
                                          |> Option.defaultValue String.Empty

                                      yield (condition, package), value
                                  | _ -> () ]
                            |> Map.ofList

                        let proposed =
                            grouped
                            |> List.map (fun ((condition, package), values) ->
                                (condition, package), (values |> Seq.head).Value)

                        if
                            proposed
                            |> List.exists (fun (key, value) ->
                                existing |> Map.tryFind key |> Option.exists ((<>) value))
                        then
                            Error "The root central package file has a conflicting condition/package version."
                        else
                            for reference in descendants "PackageReference" projectDocument do
                                attribute "Version" reference |> Option.iter _.Remove()

                                reference.Elements()
                                |> Seq.filter (fun element -> element.Name.LocalName = "Version")
                                |> Seq.toList
                                |> List.iter _.Remove()

                            for (condition, package), value in proposed do
                                if not (existing.ContainsKey(condition, package)) then
                                    let group =
                                        ownerDocument.Root.Elements(name "ItemGroup")
                                        |> Seq.tryFind (fun item ->
                                            (textAttribute "Condition" item |> Option.defaultValue String.Empty) = condition)
                                        |> Option.defaultWith (fun () ->
                                            let item = XElement(name "ItemGroup")

                                            if not (String.IsNullOrEmpty condition) then
                                                item.SetAttributeValue(name "Condition", condition)

                                            ownerDocument.Root.Add item
                                            item)

                                    group.Add(
                                        XElement(
                                            name "PackageVersion",
                                            XAttribute(name "Include", package),
                                            XAttribute(name "Version", value)
                                        )
                                    )

                            let encode (document: XDocument) =
                                Encoding.UTF8.GetBytes(document.ToString())

                            Ok [ project, encode projectDocument; owner, encode ownerDocument ]
        with
        | :? IOException as error -> Error error.Message
        | :? System.Xml.XmlException as error -> Error error.Message
