namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Project-folder scenarios")>]
type VirtualProjectFolderTests() =
    [<Fact>]
    member _.``links an external folder at a nested virtual path without creating local directories``
        ()
        =
        let external = WorkspaceRpcScenario.temporaryDirectory "nested-virtual-link-source"
        File.WriteAllText(Path.Combine(external, "Source.txt"), "source")

        let session =
            WorkspaceRpcScenario.openProject
                "nested-virtual-link"
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"

        try
            WorkspaceRpcScenario.previewAndExecute
                session.Child
                3u
                "project.folder.link"
                session.ProjectId
                (WorkspaceRpcScenario.map
                    [ "source", RpcValue.String external
                      "path", RpcValue.String "Virtual/Linked"
                      "itemType", RpcValue.String "Content" ])
                0L
                true

            let project = File.ReadAllText session.Project

            (project)
            |> should
                haveSubstring
                ("<Link>Virtual/Linked/%(RecursiveDir)%(Filename)%(Extension)</Link>")

            Directory.Exists(Path.Combine(session.Directory, "Virtual"))
            |> should equal false
        finally
            WorkspaceRpcScenario.closeProject session
            Directory.Delete(external, true)
