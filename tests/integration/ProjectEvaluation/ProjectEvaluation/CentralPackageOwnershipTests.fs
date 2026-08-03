namespace Dotnet.WorkspaceExplorer.ProjectEvaluation.IntegrationTests

#nowarn "3261"

open System.IO
open Dotnet.WorkspaceExplorer.Rpc
open FsUnit.Xunit
open Xunit

[<Collection("Project evaluation scenarios")>]
type CentralPackageOwnershipTests() =
    [<Fact>]
    member _.``central package evaluation retains owner paths and combined item-group and item conditions for package membership``
        ()
        =
        let directory = Test.temporaryDirectory "package-ownership"

        try
            let root = Path.Combine(directory, "Directory.Packages.props")
            let project = Path.Combine(directory, "App.csproj")

            Test.write
                root
                """
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup Condition=" '$(TargetFramework)' == 'net8.0' ">
    <PackageVersion Include="Example" Version="1.2.3" Condition=" '$(Configuration)' == 'Debug' " />
  </ItemGroup>
</Project>
"""

            Test.write
                project
                """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup Condition=" '$(TargetFramework)' == 'net8.0' ">
    <PackageReference Include="Example" Condition=" '$(Configuration)' == 'Debug' " />
  </ItemGroup>
</Project>
"""

            Test.withWorker directory (fun worker ->
                let error, snapshot = Test.evaluate worker 2u project
                error.IsNone |> should equal true

                let dimension =
                    Test.values "dimensions" snapshot
                    |> Seq.find (fun value ->
                        Test.field "targetFramework" value = RpcValue.String "net8.0")

                let membership = Test.values "packageMemberships" dimension |> Seq.exactlyOne
                let owner = Test.values "packageVersions" dimension |> Seq.exactlyOne
                Test.stringField "declaringPath" membership |> should equal project
                Test.stringField "declaringPath" owner |> should equal root

                Test.stringField "condition" membership
                |> should
                    equal
                    " '$(TargetFramework)' == 'net8.0'  AND  '$(Configuration)' == 'Debug' "

                Test.stringField "condition" owner
                |> should
                    equal
                    " '$(TargetFramework)' == 'net8.0'  AND  '$(Configuration)' == 'Debug' "

                3u)
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
