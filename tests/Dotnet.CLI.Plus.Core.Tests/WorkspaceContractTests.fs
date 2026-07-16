namespace Dotnet.CLI.Plus.Core.Tests

open System
open System.Collections.Immutable
open Dotnet.CLI.Plus.Core
open Xunit

module private Helpers =
    let diagnostic () =
        WorkspaceDiagnostic.Create(
            WorkspaceDiagnosticSeverity.Error,
            WorkspaceDiagnosticCode.Create "workspace.test",
            "Safe test diagnostic.",
            None,
            None,
            false,
            CorrelationId.New()
        )

    let workspace format access =
        WorkspaceDescriptor.Create(
            WorkspaceTargetPath.Create "/tmp/dotnet-cli-plus/Demo.slnx",
            HostFileSystemCaseSemantics.Sensitive,
            format,
            WorkspaceRevision.Create 1L,
            access
        )

type WorkspaceIdentityTests() =
    [<Fact>]
    member _.``workspace identity is deterministic and honours host case semantics``() =
        let upper = WorkspaceTargetPath.Create "/tmp/dotnet-cli-plus/Demo.slnx"
        let lower = WorkspaceTargetPath.Create "/tmp/dotnet-cli-plus/demo.slnx"

        Assert.Equal(
            WorkspaceId.Create(upper, HostFileSystemCaseSemantics.Insensitive).Value,
            WorkspaceId.Create(lower, HostFileSystemCaseSemantics.Insensitive).Value
        )

        Assert.NotEqual<string>(
            WorkspaceId.Create(upper, HostFileSystemCaseSemantics.Sensitive).Value,
            WorkspaceId.Create(lower, HostFileSystemCaseSemantics.Sensitive).Value
        )

    [<Fact>]
    member _.``node identities persist only while semantic identity persists``() =
        let workspace = Helpers.workspace WorkspaceFormat.Slnx WorkspaceAccess.ReadWrite

        let create identity =
            WorkspaceNode.Create(
                workspace,
                WorkspaceNodeKind.Project,
                NodeSemanticIdentity.Create identity,
                "Demo",
                WorkspaceCapabilityProfile.Full
            )

        let first = create "src\\Demo\\Demo.csproj"
        let reloaded = create "src/Demo/Demo.csproj"
        let renamed = create "src/Demo/Renamed.csproj"
        Assert.Equal(first.NodeId.Value, reloaded.NodeId.Value)
        Assert.NotEqual<string>(first.NodeId.Value, renamed.NodeId.Value)

        let replacement =
            { OldId = first.NodeId
              NewId = renamed.NodeId }

        Assert.Equal(first.NodeId.Value, replacement.OldId.Value)
        Assert.Equal(renamed.NodeId.Value, replacement.NewId.Value)

type WorkspaceInvariantTests() =
    [<Fact>]
    member _.``solution filters and placeholders are always read-only``() =
        let filteredWorkspace =
            Helpers.workspace WorkspaceFormat.Slnf WorkspaceAccess.ReadWrite

        let node =
            WorkspaceNode.Create(
                filteredWorkspace,
                WorkspaceNodeKind.Project,
                NodeSemanticIdentity.Create "Demo.csproj",
                "Demo",
                WorkspaceCapabilityProfile.Full
            )

        let placeholder =
            WorkspaceNode.Create(
                Helpers.workspace WorkspaceFormat.Slnx WorkspaceAccess.ReadWrite,
                WorkspaceNodeKind.Placeholder,
                NodeSemanticIdentity.Create "filtered/Demo.csproj",
                "Demo",
                WorkspaceCapabilityProfile.Full
            )

        Assert.True(filteredWorkspace.IsReadOnly)
        Assert.Equal(WorkspaceAccess.ReadOnly, filteredWorkspace.WorkspaceAccess)
        Assert.False(node.Supports WorkspaceCapabilityId.Write)
        Assert.False(placeholder.Supports WorkspaceCapabilityId.Write)

    [<Fact>]
    member _.``unknown project systems expose only read capability``() =
        let node =
            WorkspaceNode.Create(
                Helpers.workspace WorkspaceFormat.Slnx WorkspaceAccess.ReadWrite,
                WorkspaceNodeKind.Project,
                NodeSemanticIdentity.Create "UnknownProject.csproj",
                "Unknown project",
                WorkspaceCapabilityProfile.UnknownProjectSystem
            )

        Assert.True(node.Supports WorkspaceCapabilityId.Read)
        Assert.False(node.Supports WorkspaceCapabilityId.Write)

    [<Fact>]
    member _.``revision preconditions return a typed conflict without advancing``() =
        let expected = WorkspaceRevision.Create 5L
        let actual = WorkspaceRevision.Create 6L

        match WorkspaceRevisionPrecondition.Check(expected, actual, Helpers.diagnostic ()) with
        | Failure(Conflict(conflictExpected, conflictActual, _)) ->
            Assert.Equal(expected.Value, conflictExpected.Value)
            Assert.Equal(actual.Value, conflictActual.Value)
        | _ -> failwith "Expected a workspace conflict."

type CommandAndOutcomeTests() =
    [<Fact>]
    member _.``command descriptors retain typed parameters and expected revisions``() =
        let parameter =
            CommandParameterDescriptor.Create(
                CommandParameterId.Create "projectPath",
                CommandParameterType.Path,
                true,
                "Project path"
            )

        let descriptor =
            CommandDescriptor.Create(
                CommandId.Create "solution.add-project",
                "Add project",
                CommandAccess.Write,
                [ parameter ],
                [ WorkspaceNodeKind.Workspace ]
            )

        let arguments =
            CommandArguments.Create
                [ { ParameterId = parameter.ParameterId
                    Value = Path(WorkspaceArtifactPath.Create "/tmp/dotnet-cli-plus/Demo.csproj") } ]

        let request =
            { CommandId = descriptor.CommandId
              TargetId = None
              Arguments = arguments
              ExpectedRevision = WorkspaceRevision.Create 7L }

        Assert.Equal(WorkspaceCapabilityId.Write.Value, descriptor.RequiredCapability.Value)
        Assert.Equal(CommandParameterType.Path, descriptor.ParameterDescriptors[0].ParameterType)
        Assert.Equal(7L, request.ExpectedRevision.Value)

    [<Fact>]
    member _.``outcomes expose every stable error code``() =
        let diagnostic = Helpers.diagnostic ()

        let failures =
            [ InvalidInput("path", diagnostic)
              UnsupportedCapability(WorkspaceCapabilityId.Write, diagnostic)
              NotFound("node", diagnostic)
              AmbiguousTarget("solution", diagnostic)
              Conflict(WorkspaceRevision.Create 2L, WorkspaceRevision.Create 3L, diagnostic)
              Cancelled(OperationId.New(), diagnostic)
              ExternalToolFailed("dotnet", 1, diagnostic)
              PartialRecoveryRequired("restore backup", diagnostic)
              Internal diagnostic ]

        let actual = failures |> List.map (fun failure -> failure.Code.Value)

        Assert.Equal<string list>(
            [ "invalid_input"
              "unsupported_capability"
              "not_found"
              "ambiguous_target"
              "workspace_conflict"
              "cancelled"
              "external_tool_failed"
              "partial_recovery_required"
              "internal_error" ],
            actual
        )

    [<Fact>]
    member _.``outcomes expose exhaustive delegate matching for C# consumers``() =
        let outcome: WorkspaceOutcome<string> =
            Failure(InvalidInput("path", Helpers.diagnostic ()))

        let matched =
            outcome.Match(
                Func<string, string>(fun _ -> "success"),
                Func<string, WorkspaceDiagnostic, string>(fun _ _ -> "invalid_input"),
                Func<WorkspaceCapabilityId, WorkspaceDiagnostic, string>(fun _ _ -> "unsupported_capability"),
                Func<string, WorkspaceDiagnostic, string>(fun _ _ -> "not_found"),
                Func<string, WorkspaceDiagnostic, string>(fun _ _ -> "ambiguous_target"),
                Func<WorkspaceRevision, WorkspaceRevision, WorkspaceDiagnostic, string>(fun _ _ _ ->
                    "workspace_conflict"),
                Func<OperationId, WorkspaceDiagnostic, string>(fun _ _ -> "cancelled"),
                Func<string, int, WorkspaceDiagnostic, string>(fun _ _ _ -> "external_tool_failed"),
                Func<string, WorkspaceDiagnostic, string>(fun _ _ -> "partial_recovery_required"),
                Func<WorkspaceDiagnostic, string>(fun _ -> "internal_error")
            )

        Assert.Equal("invalid_input", matched)

    [<Fact>]
    member _.``roots pages and exports retain their revision``() =
        let revision = WorkspaceRevision.Create 4L

        let node =
            WorkspaceNode.Create(
                Helpers.workspace WorkspaceFormat.Slnx WorkspaceAccess.ReadWrite,
                WorkspaceNodeKind.Workspace,
                NodeSemanticIdentity.Create "root",
                "Demo",
                WorkspaceCapabilityProfile.Full
            )

        let root =
            { Revision = revision
              Nodes = ImmutableArray.Create node }

        let page =
            { Revision = revision
              ParentId = node.NodeId
              Nodes = ImmutableArray<WorkspaceNode>.Empty
              NextToken = None }

        let export =
            { Revision = revision
              Nodes = ImmutableArray.Create node }

        Assert.Equal(revision.Value, root.Revision.Value)
        Assert.Equal(revision.Value, page.Revision.Value)
        Assert.Equal(revision.Value, export.Revision.Value)

type ArchitectureDependencyFirewallTests() =
    [<Fact>]
    member _.``core has no forbidden implementation dependencies or namespaces``() =
        let forbidden =
            [ "Spectre"
              "MessagePack"
              "Neovim"
              "Microsoft.VisualStudio.SolutionPersistence"
              "Microsoft.Build" ]

        let startsWithForbidden (value: string) =
            forbidden
            |> List.exists (fun prefix -> value.StartsWith(prefix, StringComparison.Ordinal))

        let assembly = typeof<WorkspaceId>.Assembly
        let dependencies = assembly.GetReferencedAssemblies() |> Array.map _.Name

        let namespaces =
            assembly.GetExportedTypes()
            |> Array.choose (fun item -> Option.ofObj item.Namespace)

        Assert.False(dependencies |> Array.exists (Option.ofObj >> Option.exists startsWithForbidden))
        Assert.False(namespaces |> Array.exists startsWithForbidden)
