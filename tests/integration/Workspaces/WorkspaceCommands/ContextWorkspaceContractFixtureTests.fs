namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System
open System.Collections.Immutable
open System.IO
open System.Text
open System.Threading
open Dotnet.WorkspaceExplorer
open Dotnet.WorkspaceExplorer.Rpc
open Dotnet.WorkspaceExplorer.WorkspaceCommands
open Dotnet.WorkspaceExplorer.WorkspaceEditing
open Dotnet.WorkspaceExplorer.WorkspaceIndex
open Dotnet.WorkspaceExplorer.Workspaces
open Xunit

type private ContextNoTrash() =
    interface ArtifactTrash with
        member _.MoveToTrash _ = Ok()

[<Collection("Workspace scenarios")>]
type ContextWorkspaceContractFixtureTests() =
    let template identity name shortName language templateType precedence =
        $$"""
          {
            "Identity": "{{identity}}",
            "Name": "{{name}}",
            "ShortNameList": ["{{shortName}}"],
            "Precedence": {{precedence}},
            "Description": "{{name}} description",
            "TagsCollection": {
              "language": "{{language}}",
              "type": "{{templateType}}"
            }
          }
          """

    let catalogJson templates =
        let joined = String.concat "," templates
        $$"""{"TemplateInfo":[{{joined}}]}""" |> Encoding.UTF8.GetBytes

    let parsed fingerprint templates =
        match WorkspaceTemplateCatalog.parseBytes fingerprint (catalogJson templates) with
        | Ok entries -> entries
        | Error error -> failwithf "Catalog parsing failed: %s: %s" error.Code error.Message

    let commandArguments entry =
        ContextWorkspaceActions.templateArguments
            entry
            "Generated"
            (WorkspaceArtifactPath.Create(Path.GetFullPath "Generated"))
            None

    let forwardedArguments (arguments: CommandArguments) =
        arguments.Values
        |> Seq.find (fun argument -> argument.ParameterId.Value = "arguments")
        |> fun argument ->
            match argument.Value with
            | TextArray values -> values |> Seq.toArray
            | value -> failwithf "Expected forwarded text arguments, got %A" value

    let descriptor =
        WorkspaceDescriptor.Create(
            WorkspacePath.Create(Path.GetFullPath "Fixture.slnx"),
            FileSystemCaseSensitivity.Sensitive,
            WorkspaceFormat.Slnx,
            WorkspaceRevision.Create 0L,
            WorkspaceAccess.ReadWrite
        )

    let node kind loadState =
        WorkspaceNode.CreateWithLoadState(
            descriptor,
            kind,
            WorkspaceNodeIdentity.Create(string kind),
            string kind,
            WorkspaceCapabilityProfile.Full,
            loadState
        )

    let commandIds readOnly target =
        ContextWorkspaceCommands.discover readOnly (Some target)
        |> Seq.map _.Id.Value
        |> Set.ofSeq

    [<Fact>]
    member _.``catalog fixtures preserve type collisions and language variants deterministically``
        ()
        =
        let entries =
            parsed
                "catalog-a"
                [ template "old-csharp" "Old interface" "contract" "C#" "item" 100
                  template "custom-csharp" "Custom interface" "contract" "C#" "item" 200
                  template "fsharp" "F# contract" "contract" "F#" "item" 200
                  template "visual-basic" "VB contract" "contract" "VB" "item" 200
                  template "project-collision" "Custom project" "contract" "C#" "project" 200 ]

        Assert.Equal(4, entries.Length)

        let item =
            entries
            |> Array.find (fun entry ->
                entry.ShortName = "contract"
                && entry.Language = Some "C#"
                && entry.Kind = WorkspaceCreateKind.ItemTemplate)

        Assert.Equal("custom-csharp", item.Identity)

        Assert.Contains(
            entries,
            fun entry -> entry.Language = Some "F#" && entry.Kind = WorkspaceCreateKind.ItemTemplate
        )

        Assert.Contains(
            entries,
            fun entry -> entry.Language = Some "VB" && entry.Kind = WorkspaceCreateKind.ItemTemplate
        )

        let project =
            entries
            |> Array.find (fun entry -> entry.Kind = WorkspaceCreateKind.ProjectTemplate)

        let itemArguments = commandArguments item |> forwardedArguments
        let projectArguments = commandArguments project |> forwardedArguments

        Assert.Contains("--type", itemArguments)
        Assert.Equal("item", itemArguments[Array.IndexOf(itemArguments, "--type") + 1])
        Assert.DoesNotContain("--no-restore", itemArguments)
        Assert.Equal("project", projectArguments[Array.IndexOf(projectArguments, "--type") + 1])
        Assert.Contains("--no-restore", projectArguments)

    [<Fact>]
    member _.``catalog fixtures reject ambiguity and invalidate opaque bindings``() =
        let templates =
            [ template "first" "First" "ambiguous" "C#" "item" 300
              template "second" "Second" "ambiguous" "C#" "item" 300 ]

        match WorkspaceTemplateCatalog.parseBytes "catalog-a" (catalogJson templates) with
        | Error error -> Assert.Equal("template_catalog_ambiguous", error.Code)
        | Ok entries -> failwithf "Expected catalog ambiguity, got %A" entries

        let stable = [ template "custom" "Custom item" "custom" "C#" "item" 100 ]

        let entry = parsed "catalog-a" stable |> Array.exactlyOne
        let changedEntries = parsed "catalog-b" stable

        let changedCatalog =
            { Fingerprint = "catalog-b"
              EmptySelectionId = "changed-empty"
              Entries = changedEntries }

        match
            WorkspaceTemplateCatalog.validateBinding
                (WorkspaceTemplateCatalog.binding entry)
                changedCatalog
        with
        | Error error -> Assert.Equal("template_catalog_changed", error.Code)
        | Ok() -> failwith "Expected a changed catalog binding to be rejected."

    [<Fact>]
    member _.``context command descriptors and semantic matrices are exact``() =
        let createKinds =
            [ WorkspaceNodeKind.Workspace
              WorkspaceNodeKind.SolutionFolder
              WorkspaceNodeKind.SolutionItem
              WorkspaceNodeKind.Project
              WorkspaceNodeKind.ProjectFolder
              WorkspaceNodeKind.ProjectFile
              WorkspaceNodeKind.DependencyContainer
              WorkspaceNodeKind.Dependency ]

        let deleteKinds =
            [ WorkspaceNodeKind.SolutionFolder
              WorkspaceNodeKind.SolutionItem
              WorkspaceNodeKind.Project
              WorkspaceNodeKind.ProjectFolder
              WorkspaceNodeKind.ProjectFile ]

        for kind in Enum.GetValues<WorkspaceNodeKind>() do
            let commands = commandIds false (node kind WorkspaceNodeLoadState.Hydrated)
            Assert.Equal(List.contains kind createKinds, commands.Contains "workspace.create")
            Assert.Equal(List.contains kind deleteKinds, commands.Contains "workspace.delete")

        Assert.Empty(
            ContextWorkspaceCommands.discover
                false
                (Some(node WorkspaceNodeKind.Project WorkspaceNodeLoadState.FilteredOut))
        )

        Assert.Empty(
            ContextWorkspaceCommands.discover
                true
                (Some(node WorkspaceNodeKind.Project WorkspaceNodeLoadState.Hydrated))
        )

        let createParameters =
            ContextWorkspaceCommands.create.Parameters
            |> Seq.map (fun parameter -> parameter.Id.Value, parameter.Type, parameter.Required)
            |> Seq.toArray

        let expectedCreateParameters =
            [| "selectionId", CommandParameterType.Text, true
               "name", CommandParameterType.Text, true |]

        Assert.True((createParameters = expectedCreateParameters))

        Assert.Empty(ContextWorkspaceCommands.delete.Parameters)

        let entries =
            parsed
                "catalog"
                [ template "csharp-item" "C# item" "item" "C#" "item" 100
                  template "fsharp-item" "F# item" "item" "F#" "item" 100
                  template "vb-item" "VB item" "item" "VB" "item" 100
                  template "neutral-item" "Neutral item" "neutral" "" "item" 100
                  template "csharp-project" "C# project" "project" "C#" "project" 100
                  template "fsharp-project" "F# project" "project" "F#" "project" 100
                  template "vb-project" "VB project" "project" "VB" "project" 100 ]

        let catalog =
            { Fingerprint = "catalog"
              EmptySelectionId = "empty"
              Entries = entries }

        let rootContext: WorkspaceSemanticContext =
            { Node = node WorkspaceNodeKind.Workspace WorkspaceNodeLoadState.Hydrated
              ProjectId = None
              ProjectPath = None
              PhysicalPath = None
              PhysicalDirectory = None
              LogicalFolderId = None
              LogicalFolderPath = None }

        let projectNode = node WorkspaceNodeKind.Project WorkspaceNodeLoadState.Hydrated

        let projectContext: WorkspaceSemanticContext =
            { rootContext with
                Node = projectNode
                ProjectId = Some projectNode.Id
                ProjectPath = Some(WorkspaceArtifactPath.Create(Path.GetFullPath "Fixture.csproj"))
                PhysicalDirectory = Some(WorkspaceArtifactPath.Create(Path.GetFullPath ".")) }

        let rootOptions = WorkspaceTemplateCatalog.options rootContext catalog

        Assert.All(
            rootOptions,
            fun option -> Assert.Equal(WorkspaceCreateKind.ProjectTemplate, option.Kind)
        )

        let projectOptions = WorkspaceTemplateCatalog.options projectContext catalog
        Assert.Contains(projectOptions, fun option -> option.Kind = WorkspaceCreateKind.Empty)

        Assert.DoesNotContain(
            projectOptions,
            fun option ->
                option.Kind = WorkspaceCreateKind.ItemTemplate
                && option.Language |> Option.exists ((<>) "C#")
        )

        Assert.Equal(
            3,
            projectOptions
            |> Array.filter (fun option -> option.Kind = WorkspaceCreateKind.ProjectTemplate)
            |> Array.length
        )

    [<Fact>]
    member _.``transactional publication compensates nested parent directories on failure``() =
        let root = WorkspaceRpcScenario.temporaryDirectory "nested-template-publication"

        try
            let nested = Path.Combine(root, "generated", "contracts")
            let destination = Path.Combine(nested, "IContract.cs")
            let unavailable = Path.Combine(root, "missing", "failure.cs")

            let coordinator =
                WorkspaceEditTransaction(
                    WorkspaceArtifactPath.Create root,
                    TimeProvider.System,
                    (fun () -> WorkspaceRevision.Create 0L),
                    ContextNoTrash()
                )

            let request =
                { CommandId = CommandId.Create "template.create"
                  Targets =
                    [ Path.Combine(root, "generated"); nested; destination; unavailable ]
                    |> Seq.map WorkspaceArtifactPath.Create
                    |> ImmutableArray.CreateRange
                  Arguments = CommandArguments.Create []
                  ExpectedRevision = WorkspaceRevision.Create 0L
                  Intents = ImmutableHashSet<WorkspaceEditIntent>.Empty
                  AuthorizedRoots = ImmutableArray.Create(WorkspaceArtifactPath.Create root) }

            let actions =
                [ WorkspaceEditAction.CreateDirectory(Path.Combine(root, "generated"))
                  WorkspaceEditAction.CreateDirectory nested
                  WorkspaceEditAction.ReplaceFile(destination, Encoding.UTF8.GetBytes "contract")
                  WorkspaceEditAction.ReplaceFile(unavailable, Encoding.UTF8.GetBytes "failure") ]

            let preview =
                match coordinator.Prepare(request, actions) with
                | Success value -> value
                | Failure failure -> failwithf "Preview failed: %A" failure

            match
                coordinator.Execute(request, actions, preview.Confirmation, CancellationToken.None)
            with
            | Success(RolledBack _) -> ()
            | outcome -> failwithf "Expected compensated failure, got %A" outcome

            Assert.False(Directory.Exists(Path.Combine(root, "generated")))
            Assert.False(File.Exists destination)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)

    [<Fact>]
    member _.``complete output enumeration exposes postaction artifacts``() =
        let root = WorkspaceRpcScenario.temporaryDirectory "template-output-equality"

        try
            let project = Path.Combine(root, "Generated.csproj")
            let postaction = Path.Combine(root, "obj", "project.assets.json")
            Directory.CreateDirectory(Path.GetDirectoryName postaction) |> ignore
            File.WriteAllText(project, "<Project />")
            File.WriteAllText(postaction, "{}")

            match DotnetCommandCompensation.outputArtifacts root with
            | Error error -> failwith error
            | Ok outputs ->
                let expected =
                    [| Path.GetFullPath project
                       Path.GetFullPath(Path.GetDirectoryName postaction)
                       Path.GetFullPath postaction |]
                    |> Array.sort

                Assert.True((expected = outputs))
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
