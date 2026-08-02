namespace Dotnet.WorkspaceExplorer.WorkspaceIndex

open System
open System.Collections.Immutable
open System.IO
open System.Xml.Linq
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.ProjectEvaluation

type internal ExploredProjectProperty =
    { Name: string
      Scope: WorkspaceArtifactPath
      Condition: string option
      Value: string }

type internal EvaluatedWorkspaceProject =
    { Snapshot: ProjectEvaluationSnapshot
      DeclaredProperties: ImmutableArray<ExploredProjectProperty> }

module internal ExploredProjectProperties =
    let Names =
        Set.ofList
            [ "AssemblyName"
              "RootNamespace"
              "OutputType"
              "TargetFramework"
              "TargetFrameworks"
              "RuntimeIdentifier"
              "RuntimeIdentifiers"
              "LangVersion"
              "TreatWarningsAsErrors"
              "IsPackable"
              "PackageId"
              "Version"
              "SignAssembly"
              "AssemblyOriginatorKeyFile"
              "SelfContained"
              "PublishSingleFile"
              "PublishTrimmed"
              "PublishAot" ]

    let private generated directory path =
        let relative = Path.GetRelativePath(directory, path).Replace('\\', '/')

        relative.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
        || relative.Equals(".generated", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(".generated/", StringComparison.OrdinalIgnoreCase)
        || relative.EndsWith("/.generated", StringComparison.OrdinalIgnoreCase)
        || relative.Contains("/.generated/", StringComparison.OrdinalIgnoreCase)

    let private attribute local (element: XElement) =
        element.Attribute(XName.Get local) |> Option.ofObj |> Option.map _.Value

    let eligibleScopes
        (workspacePath: WorkspaceArtifactPath)
        (snapshot: ProjectEvaluationSnapshot)
        =
        let workspaceDirectory =
            Path.GetDirectoryName workspacePath.Value
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        let projectDirectory =
            Path.GetDirectoryName snapshot.ProjectPath.Value
            |> Option.ofObj
            |> Option.defaultValue workspaceDirectory

        let toolchainRoots = ProjectInputClassification.toolchainRoots snapshot

        snapshot.Imports
        |> Seq.filter (fun path ->
            File.Exists path.Value
            && not (ProjectInputClassification.isToolchainPath toolchainRoots path.Value)
            && not (generated projectDirectory path.Value))
        |> Seq.distinct
        |> ImmutableArray.CreateRange

    let isEligibleScope
        (workspacePath: WorkspaceArtifactPath)
        (snapshot: ProjectEvaluationSnapshot)
        (path: WorkspaceArtifactPath)
        =
        eligibleScopes workspacePath snapshot
        |> Seq.exists (fun candidate -> candidate.Value = path.Value)

    let declarations workspacePath snapshot =
        try
            eligibleScopes workspacePath snapshot
            |> Seq.collect (fun path ->
                let document = XDocument.Load(path.Value, LoadOptions.PreserveWhitespace)

                document.Descendants()
                |> Seq.choose (fun property ->
                    if
                        Names.Contains property.Name.LocalName
                        && (attribute "Condition" property).IsNone
                    then
                        property.Parent
                        |> Option.ofObj
                        |> Option.bind (fun group ->
                            if group.Name = XName.Get "PropertyGroup" then
                                Some(
                                    { Name = property.Name.LocalName
                                      Scope = path
                                      Condition = attribute "Condition" group
                                      Value = property.Value }
                                    : ExploredProjectProperty
                                )
                            else
                                None)
                    else
                        None))
            |> Seq.distinct
            |> ImmutableArray.CreateRange
            |> Ok
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? Xml.XmlException -> Error "Project declarations could not be read."

    let hasImportedProperty workspacePath snapshot propertyName =
        try
            eligibleScopes workspacePath snapshot
            |> Seq.filter (fun path -> path.Value <> snapshot.ProjectPath.Value)
            |> Seq.exists (fun path ->
                let document = XDocument.Load(path.Value, LoadOptions.PreserveWhitespace)

                document.Descendants(XName.Get propertyName)
                |> Seq.exists (fun property ->
                    property.Parent
                    |> Option.ofObj
                    |> Option.exists (fun group -> group.Name = XName.Get "PropertyGroup")))
            |> Ok
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? Xml.XmlException -> Error "Project declarations could not be read."
