namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

#nowarn "3261"

open System
open System.IO
open System.Threading
open System.Xml.Linq
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceIndex

module internal ProjectPropertyEditPlanning =
    open MsBuildProjectDocument
    open ProjectEditPlanning

    let private booleanProperties =
        Set.ofList
            [ "TreatWarningsAsErrors"
              "IsPackable"
              "SignAssembly"
              "SelfContained"
              "PublishSingleFile"
              "PublishTrimmed"
              "PublishAot" ]

    let private validateProperty projectDirectory name value =
        if
            not (ExploredProjectProperties.Names.Contains name)
            || String.IsNullOrWhiteSpace value
        then
            Error "The property name or value is not in the curated registry."
        elif
            booleanProperties.Contains name
            && not (
                String.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || String.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            )
        then
            Error $"'{name}' requires a boolean value."
        elif
            name = "OutputType"
            && not (Set.contains value (Set.ofList [ "Exe"; "Library"; "WinExe"; "Module" ]))
        then
            Error "OutputType must be Exe, Library, WinExe, or Module."
        elif name = "AssemblyOriginatorKeyFile" then
            let keyPath = Path.GetFullPath(value, projectDirectory)
            let relative = Path.GetRelativePath(projectDirectory, keyPath)

            let outside =
                Path.IsPathRooted relative
                || relative = ".."
                || relative.StartsWith $"..{Path.DirectorySeparatorChar}"
                || relative.StartsWith $"..{Path.AltDirectorySeparatorChar}"

            if Path.IsPathRooted value || outside then
                Error "AssemblyOriginatorKeyFile must stay within the project directory."
            else
                Ok()
        else
            Ok()

    let plan
        (workspace: SolutionWorkspace)
        (project: SolutionProject)
        (snapshot: ProjectEvaluationSnapshot)
        (command: CommandMutationRequest)
        (_: CancellationToken)
        =
        if snapshot.CapabilityProfile <> WorkspaceCapabilityProfile.Full then
            unsupported "The evaluated project system does not grant project write capability."
        elif command.TargetWorkspaceNodeId <> Some project.Node.Id then
            missing "targetNodeId" "The command target was not found."
        elif ProjectPropertyCommands.tryDescribe command.CommandId |> Option.isNone then
            missing "commandId" "The command is not available."
        else
            try
                let projectPath = project.Path.AbsolutePath.Value

                let directory =
                    Path.GetDirectoryName projectPath
                    |> Option.ofObj
                    |> Option.defaultValue (Directory.GetCurrentDirectory())

                let document, encoding, preamble, lineEnding = readDocument projectPath

                let propertyName = requiredChoice "name" command.Arguments |> unwrap
                let propertyValue = requiredText "value" command.Arguments |> unwrap

                validateProperty directory propertyName propertyValue |> unwrap

                let scope =
                    match value "scope" command.Arguments with
                    | None ->
                        let importedDeclares =
                            ExploredProjectProperties.hasImportedProperty
                                workspace.SolutionPath
                                snapshot
                                propertyName
                            |> Result.defaultWith (fun message -> raise (IOException message))

                        if importedDeclares then
                            raise (
                                ArgumentException(
                                    "The property is declared in an import; "
                                    + "supply its explicit writable scope and "
                                    + "condition (use an empty condition for an "
                                    + "unconditional group)."
                                )
                            )

                        projectPath
                    | Some(Path path) ->
                        let full = Path.GetFullPath(path.Value, directory)

                        if
                            full <> projectPath
                            && not (
                                ExploredProjectProperties.isEligibleScope
                                    workspace.SolutionPath
                                    snapshot
                                    (WorkspaceArtifactPath.Create full)
                            )
                        then
                            raise (
                                ArgumentException
                                    "The writable scope is not an eligible project import."
                            )

                        full
                    | _ -> raise (ArgumentException "'scope' must be a path.")

                let suppliedCondition = optionalText "condition" command.Arguments |> unwrap

                let condition =
                    suppliedCondition
                    |> Option.bind (fun value -> if value.Length = 0 then None else Some value)

                let scopeDocument, scopeEncoding, scopePreamble, scopeLineEnding =
                    if scope = projectPath then
                        document, encoding, preamble, lineEnding
                    else
                        readDocument scope

                let matches = scopeDocument.Descendants(name propertyName) |> Seq.toArray

                if
                    matches |> Seq.exists (fun property -> (attribute "Condition" property).IsSome)
                then
                    raise (ArgumentException "Property-level conditions are not supported.")

                let groups = matches |> Seq.map _.Parent |> Seq.distinct |> Seq.toArray

                let hasSingleConditionalGroup () =
                    groups.Length = 1 && (attribute "Condition" groups[0]).IsSome

                if
                    suppliedCondition.IsNone
                    && (groups.Length > 1 || scope <> projectPath || hasSingleConditionalGroup ())
                then
                    raise (
                        ArgumentException(
                            "The property scope is ambiguous; supply an explicit "
                            + "condition (or an empty condition for an "
                            + "unconditional group)."
                        )
                    )

                let group =
                    if suppliedCondition.IsNone && groups.Length = 1 then
                        groups[0]
                    else
                        scopeDocument.Root.Elements(name "PropertyGroup")
                        |> Seq.tryFind (fun element -> attribute "Condition" element = condition)
                        |> Option.defaultWith (fun () ->
                            let value = XElement(name "PropertyGroup")

                            condition
                            |> Option.iter (fun text ->
                                value.SetAttributeValue(name "Condition", text))

                            scopeDocument.Root.Add(
                                XText $"{newline scopeDocument}  ",
                                value,
                                XText(newline scopeDocument)
                            )

                            value)

                match group.Element(name propertyName) with
                | null ->
                    group.Add(
                        XText $"{newline scopeDocument}    ",
                        XElement(name propertyName, propertyValue),
                        XText $"{newline scopeDocument}  "
                    )
                | property -> property.Value <- propertyValue

                makePlan
                    workspace
                    command
                    [ replaceProject scope scopeDocument scopeEncoding scopePreamble scopeLineEnding ]
                    [ scope ]
                    []
            with
            | :? ArgumentException as error -> invalid "command" error.Message
            | :? IOException -> invalid "project" "The project file could not be read."
            | :? UnauthorizedAccessException ->
                invalid "project" "The project file could not be read."
            | :? Xml.XmlException -> invalid "project" "The project XML is malformed."
