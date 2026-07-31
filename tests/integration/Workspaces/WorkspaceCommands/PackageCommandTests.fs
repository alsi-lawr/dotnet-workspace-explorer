namespace Dotnet.WorkspaceExplorer.Workspaces.IntegrationTests

#nowarn "3261"

open System.IO
open System.Xml.Linq
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Workspace-command scenarios")>]
type PackageCommandTests() =
    [<Fact>]
    member _.``conditional package version centralization enables central management and removes the project version``
        ()
        =
        let condition = "'$(TargetFramework)' == 'net10.0'"

        let session =
            WorkspaceCommandScenario.start "workspace-command-package" (fun directory model ->
                let project = Path.Combine(directory, "App.csproj")
                let otherCondition = "'$(TargetFramework)' == 'net9.0'"

                File.WriteAllText(
                    project,
                    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                    + "<TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                    + $"<ItemGroup Condition=\"{condition}\">"
                    + "<PackageReference Include=\"Example.Package\" Version=\"1.0.0\" />"
                    + "</ItemGroup></Project>"
                )

                File.WriteAllText(
                    Path.Combine(directory, "Directory.Packages.props"),
                    "<Project><PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\">"
                    + "<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>"
                    + $"</PropertyGroup><ItemGroup Condition=\"{condition}\">"
                    + "<PackageVersion Include=\"example.package\" Version=\"2.0.0\" />"
                    + $"</ItemGroup><ItemGroup Condition=\"{otherCondition}\">"
                    + "<PackageVersion Include=\"Example.Package\" Version=\"9.0.0\" />"
                    + "</ItemGroup></Project>"
                )

                model.AddProject("App.csproj", "App", null) |> ignore)

        try
            let arguments =
                WorkspaceCommandScenario.argumentMap
                    [ "id", RpcValue.String "Example.Package"; "version", RpcValue.String "2.0.0" ]

            let completion =
                WorkspaceCommandScenario.execute
                    session
                    3u
                    "package.update"
                    session.ProjectId
                    arguments
                    0L

            completion.Outcome |> should equal "succeeded"
            completion.Revision |> should equal 1L
            let project = XDocument.Load(Path.Combine(session.Directory, "App.csproj"))

            project.Descendants(XName.Get "PackageReference")
            |> Seq.exactlyOne
            |> _.Attribute(XName.Get "Version")
            |> isNull
            |> should equal true

            let owner =
                XDocument.Load(Path.Combine(session.Directory, "Directory.Packages.props"))

            let centralProperties =
                owner.Descendants(XName.Get "ManagePackageVersionsCentrally") |> Seq.toArray

            centralProperties.Length |> should equal 2

            centralProperties
            |> Seq.find (fun property -> isNull (property.Parent.Attribute(XName.Get "Condition")))
            |> _.Value
            |> should equal "true"

            centralProperties
            |> Seq.find (fun property ->
                not (isNull (property.Parent.Attribute(XName.Get "Condition"))))
            |> _.Value
            |> should equal "false"

            let versions = owner.Descendants(XName.Get "PackageVersion") |> Seq.toArray
            versions.Length |> should equal 2

            let version =
                versions
                |> Seq.find (fun item ->
                    item.Parent.Attribute(XName.Get "Condition").Value = condition)

            version.Attribute(XName.Get "Include").Value |> should equal "example.package"
            version.Attribute(XName.Get "Version").Value |> should equal "2.0.0"

            versions
            |> Seq.find (fun item ->
                item.Parent.Attribute(XName.Get "Condition").Value <> condition)
            |> _.Attribute(XName.Get "Version")
            |> _.Value
            |> should equal "9.0.0"
        finally
            WorkspaceCommandScenario.stop session

    [<Fact>]
    member _.``a package mutation below the selected workspace root is rejected``() =
        let session =
            WorkspaceCommandScenario.start
                "workspace-command-nested-package-owner"
                (fun directory model ->
                    let projectDirectory = Path.Combine(directory, "src")
                    Directory.CreateDirectory projectDirectory |> ignore

                    File.WriteAllText(
                        Path.Combine(projectDirectory, "App.csproj"),
                        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                        + "<TargetFramework>net10.0</TargetFramework>"
                        + "</PropertyGroup><ItemGroup>"
                        + "<PackageReference Include=\"Example.Package\" />"
                        + "</ItemGroup></Project>"
                    )

                    File.WriteAllText(
                        Path.Combine(projectDirectory, "Directory.Packages.props"),
                        "<Project><PropertyGroup>"
                        + "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>"
                        + "</PropertyGroup><ItemGroup>"
                        + "<PackageVersion Include=\"Example.Package\" Version=\"1.0.0\" />"
                        + "</ItemGroup></Project>"
                    )

                    model.AddProject("src/App.csproj", "App", null) |> ignore)

        try
            let project = Path.Combine(session.Directory, "src", "App.csproj")
            let owner = Path.Combine(session.Directory, "src", "Directory.Packages.props")
            let projectBefore = File.ReadAllBytes project
            let ownerBefore = File.ReadAllBytes owner

            WorkspaceRpcScenario.send
                session.Child
                false
                (WorkspaceRpcScenario.request
                    3u
                    "workspace/commands/preview"
                    (WorkspaceRpcScenario.map
                        [ "commandId", RpcValue.String "package.update"
                          "targetNodeId", RpcValue.String session.ProjectId.Value
                          "arguments",
                          WorkspaceCommandScenario.argumentMap
                              [ "id", RpcValue.String "Example.Package"
                                "version", RpcValue.String "2.0.0" ]
                          "expectedRevision", RpcValue.Integer 0L ]))

            let previewError, _ =
                WorkspaceRpcScenario.readFrame session.Child |> WorkspaceRpcScenario.response 3u

            previewError.Value.Code |> should equal "invalid_input"

            previewError.Value.Message
            |> should equal "A nested Directory.Packages.props owns package versions."

            WorkspaceRpcScenario.send
                session.Child
                false
                (WorkspaceRpcScenario.request 4u "workspace/root" RpcValue.emptyMap)

            let rootError, root =
                WorkspaceRpcScenario.readFrame session.Child |> WorkspaceRpcScenario.response 4u

            rootError |> should equal None

            WorkspaceRpcScenario.field "revision" root
            |> RpcValue.requireInteger "revision"
            |> should equal 0L

            File.ReadAllBytes project |> should equal projectBefore
            File.ReadAllBytes owner |> should equal ownerBefore
        finally
            WorkspaceCommandScenario.stop session
