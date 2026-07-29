namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open Xunit

[<CollectionDefinition("Delegated dotnet processes")>]
type DelegateddotnetprocessesCollection() = class end

[<CollectionDefinition("Workspace scenarios")>]
type WorkspacescenariosCollection() = class end

[<CollectionDefinition("Project-folder scenarios")>]
type ProjectfolderscenariosCollection() = class end

[<CollectionDefinition("Workspace-command scenarios")>]
type WorkspacecommandscenariosCollection() = class end

[<CollectionDefinition("Launch-profile scenarios")>]
type LaunchprofilescenariosCollection() = class end
