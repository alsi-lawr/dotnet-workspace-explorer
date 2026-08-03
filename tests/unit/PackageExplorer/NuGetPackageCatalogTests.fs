namespace Dotnet.WorkspaceExplorer.PackageExplorer.UnitTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.PackageExplorer
open Dotnet.WorkspaceExplorer.Packages
open FsUnit.Xunit
open Xunit

type private FeedResponse =
    { Status: int
      Content: string
      Delay: TimeSpan option }

type private LocalFeed(handler: string -> string -> FeedResponse) =
    let port =
        use listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        (listener.LocalEndpoint :?> IPEndPoint).Port

    let root = $"http://127.0.0.1:{port}/"
    let listener = new HttpListener()
    let cancellation = new CancellationTokenSource()
    let requests = ConcurrentQueue<string>()

    let serve () =
        task {
            while not cancellation.IsCancellationRequested do
                try
                    let! context = listener.GetContextAsync().WaitAsync(cancellation.Token)

                    let path =
                        context.Request.Url
                        |> Option.ofObj
                        |> Option.map _.AbsolutePath
                        |> Option.defaultValue "/"

                    context.Request.RawUrl |> Option.ofObj |> Option.iter requests.Enqueue

                    let response = handler root path

                    match response.Delay with
                    | Some delay -> do! Task.Delay(delay, cancellation.Token)
                    | None -> ()

                    let bytes = Encoding.UTF8.GetBytes response.Content
                    context.Response.StatusCode <- response.Status
                    context.Response.ContentType <- "application/json"
                    context.Response.ContentLength64 <- bytes.LongLength
                    do! context.Response.OutputStream.WriteAsync(bytes, cancellation.Token)
                    context.Response.Close()
                with
                | :? OperationCanceledException -> ()
                | :? HttpListenerException when cancellation.IsCancellationRequested -> ()
        }

    do
        listener.Prefixes.Add root
        listener.Start()
        Task.Run(Func<Task>(fun () -> serve () :> Task)) |> ignore

    member _.Root = root
    member _.Requests = requests |> Seq.toList

    interface IDisposable with
        member _.Dispose() =
            cancellation.Cancel()
            listener.Stop()
            listener.Close()
            cancellation.Dispose()

module private NuGetCatalogScenario =
    let response content =
        { Status = 200
          Content = content
          Delay = None }

    let status code =
        { Status = code
          Content = "{}"
          Delay = None }

    let delayed delay content =
        { Status = 200
          Content = content
          Delay = Some delay }

    let temporaryWorkspace () =
        let path =
            Path.Combine(Path.GetTempPath(), $"dotnet-we-package-catalog-{Guid.NewGuid():N}")

        Directory.CreateDirectory path |> ignore
        let project = Path.Combine(path, "Example.fsproj")
        File.WriteAllText(project, "<Project />")
        path, project

    let delete path =
        if Directory.Exists path then
            Directory.Delete(path, true)

    let target project =
        PackageWorkspaceTarget.file project |> Result.defaultWith (failwithf "%A")

    let packageId value =
        PackageId.create value |> Result.defaultWith (failwithf "%A")

    let sourceId value =
        PackageSourceId.create value |> Result.defaultWith (failwithf "%A")

    let pageSize value =
        PackagePageSize.create value |> Result.defaultWith (failwithf "%A")

    let writeConfiguration directory sources mapping credentials =
        let sourceElements =
            sources
            |> List.map (fun (name, location) ->
                $"<add key=\"{name}\" value=\"{location}\" protocolVersion=\"3\" allowInsecureConnections=\"true\" />")
            |> String.concat Environment.NewLine

        let mappingElements =
            mapping
            |> List.map (fun (source, patterns) ->
                let patternElements =
                    patterns
                    |> List.map (fun pattern -> $"<package pattern=\"{pattern}\" />")
                    |> String.concat Environment.NewLine

                $"<packageSource key=\"{source}\">{patternElements}</packageSource>")
            |> String.concat Environment.NewLine

        let credentialElements =
            credentials
            |> List.map (fun (source, user, password) ->
                $"""
                <{source}>
                    <add key="Username" value="{user}" />
                    <add key="ClearTextPassword" value="{password}" />
                </{source}>
                """)
            |> String.concat Environment.NewLine

        File.WriteAllText(
            Path.Combine(directory, "NuGet.Config"),
            $"""
            <configuration>
                <packageSources>
                    <clear />
                    {sourceElements}
                </packageSources>
                <packageSourceMapping>
                    {mappingElements}
                </packageSourceMapping>
                <packageSourceCredentials>
                    {credentialElements}
                </packageSourceCredentials>
            </configuration>
            """
        )

    let serviceIndex root includeRegistration =
        let registration =
            if includeRegistration then
                $""",{{"@id":"{root}registration/","@type":"RegistrationsBaseUrl"}},{{"@id":"{root}registration/","@type":"RegistrationsBaseUrl/3.0.0-beta"}},{{"@id":"{root}registration/","@type":"RegistrationsBaseUrl/3.0.0-rc"}},{{"@id":"{root}registration/","@type":"RegistrationsBaseUrl/3.6.0"}}"""
            else
                ""

        $"""{{"version":"3.0.0","resources":[{{"@id":"{root}query","@type":"SearchQueryService"}},{{"@id":"{root}query","@type":"SearchQueryService/3.0.0-beta"}},{{"@id":"{root}query","@type":"SearchQueryService/3.0.0-rc"}},{{"@id":"{root}query","@type":"SearchQueryService/3.5.0"}}{registration}]}}"""

    let registration root =
        $"""
        {{
            "@id": "{root}registration/example.package/index.json",
            "@type": ["catalog:CatalogRoot", "PackageRegistration"],
            "count": 1,
            "items": [
                {{
                    "@id": "{root}registration/example.package/page.json",
                    "@type": "catalog:CatalogPage",
                    "count": 1,
                    "lower": "2.0.0",
                    "upper": "2.0.0",
                    "items": [
                        {{
                            "@id": "{root}registration/example.package/2.0.0.json",
                            "@type": "Package",
                            "catalogEntry": {{
                                "@id": "{root}catalog/example.package.2.0.0.json",
                                "@type": "PackageDetails",
                                "authors": "One Author, Two Author",
                                "dependencyGroups": [
                                    {{
                                        "@type": "PackageDependencyGroup",
                                        "targetFramework": "net10.0",
                                        "dependencies": [
                                            {{
                                                "@type": "PackageDependency",
                                                "id": "Example.Dependency",
                                                "range": "[1.2.0, 2.0.0)"
                                            }}
                                        ]
                                    }}
                                ],
                                "deprecation": {{
                                    "reasons": ["Legacy"],
                                    "alternatePackage": {{
                                        "id": "Replacement.Package",
                                        "range": "[3.0.0, )"
                                    }}
                                }},
                                "description": "Detailed description",
                                "id": "Example.Package",
                                "licenseExpression": "MIT",
                                "licenseUrl": "https://licenses.example.test/mit",
                                "listed": true,
                                "projectUrl": "https://projects.example.test/package",
                                "readmeUrl": "https://readmes.example.test/package",
                                "summary": "Detailed summary",
                                "tags": ["one", "two"],
                                "version": "2.0.0",
                                "vulnerabilities": [
                                    {{
                                        "advisoryUrl": "https://advisories.example.test/one",
                                        "severity": 2
                                    }}
                                ]
                            }},
                            "packageContent": "{root}flat/example.package.2.0.0.nupkg",
                            "registration": "{root}registration/example.package/index.json"
                        }}
                    ]
                }}
            ]
        }}
        """

    let searchResult description =
        $"""
        {{
            "totalHits": 2,
            "data": [
                {{
                    "id": "Example.Package",
                    "version": "2.0.0+build.7",
                    "description": "{description}",
                    "summary": "A package summary",
                    "authors": ["Example Author"],
                    "owners": ["Example Owner"],
                    "tags": ["one", "two"],
                    "versions": [
                        {{ "version": "2.0.0+build.7", "downloads": 1 }}
                    ]
                }}
            ]
        }}
        """

    let request project value =
        { Id = PackageRequestId.newId ()
          Target = target project
          Value = value }

    let searchRequest project source continuation size =
        request
            project
            { Search =
                { Term = PackageSearchTerm.Matching "Example"
                  Prerelease = PrereleaseSelection.IncludePrerelease
                  Source = source }
              PageSize = pageSize size
              Continuation = continuation }

    let run operation =
        Async.RunSynchronously(operation, timeout = 10000)

    let value result =
        result |> Result.defaultWith (fun failure -> failwithf "%A" failure)

    let failure result =
        match result with
        | Error failure -> failure
        | Ok value -> failwithf "Expected a failure, got %A" value

[<Sealed>]
type NuGetPackageCatalogTests() =
    [<Fact>]
    member _.``configured package sources preserve effective order and never expose credentials``
        ()
        =
        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "First", "http://127.0.0.1:40101/index.json"
                  "Second", "http://127.0.0.1:40102/index.json" ]
                []
                [ "First", "private-user", "do-not-return-this-secret" ]

            let catalog = NuGetPackageCatalog.create ()

            let sources =
                catalog.ConfiguredSources(NuGetCatalogScenario.request project ())
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            sources |> List.map _.Name |> should equal [ "First"; "Second" ]

            let exposed = sprintf "%A" sources
            exposed |> should not' (haveSubstring "private-user")
            exposed |> should not' (haveSubstring "do-not-return-this-secret")
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``source mapping distinguishes a known conflict unknown transitive impact and allowed evidence``
        ()
        =
        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Contoso", "http://127.0.0.1:40201/index.json"
                  "Fallback", "http://127.0.0.1:40202/index.json" ]
                [ "Contoso", [ "Contoso.*" ]; "Fallback", [ "*" ] ]
                []

            let catalog = NuGetPackageCatalog.create ()
            let package = NuGetCatalogScenario.packageId "Contoso.Core"
            let contoso = NuGetCatalogScenario.sourceId "Contoso"

            let evaluate candidate restored =
                catalog.SourceMapping(
                    NuGetCatalogScenario.request
                        project
                        { Package = package
                          CandidateSource = candidate
                          RestoredTransitives = restored }
                )
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            evaluate (Some(NuGetCatalogScenario.sourceId "Fallback")) (Some [])
            |> should equal (PackageSourceMappingPolicy.KnownConflict(package, [ contoso ]))

            evaluate (Some contoso) None
            |> should
                equal
                (PackageSourceMappingPolicy.InsufficientRestoredTransitiveEvidence [ contoso ])

            evaluate (Some contoso) (Some [])
            |> should equal (PackageSourceMappingPolicy.Allowed [ contoso ])
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``a target without source mapping is allowed without restored transitive evidence``() =
        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Local", "http://127.0.0.1:40203/index.json" ]
                []
                []

            let catalog = NuGetPackageCatalog.create ()
            let source = NuGetCatalogScenario.sourceId "Local"

            let policy =
                catalog.SourceMapping(
                    NuGetCatalogScenario.request
                        project
                        { Package = NuGetCatalogScenario.packageId "Example.Package"
                          CandidateSource = Some source
                          RestoredTransitives = None }
                )
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            policy |> should equal (PackageSourceMappingPolicy.Allowed [ source ])
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``paged SemVer2 search normalizes oversized metadata and remains usable without registration``
        ()
        =
        let description = String('x', PackageMetadata.limits.Description + 200)

        use feed =
            new LocalFeed(fun root path ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root false)
                | "/query" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.searchResult description)
                | _ -> NuGetCatalogScenario.status 404)

        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Local", $"{feed.Root}index.json" ]
                []
                []

            let catalog = NuGetPackageCatalog.create ()

            let page =
                catalog.Search(NuGetCatalogScenario.searchRequest project None None 1)
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            page.Items |> should haveLength 1
            page.Continuation |> should not' (equal None)
            page.SourceFailures |> should be Empty
            page.Items.Head.Version.Value |> should equal "2.0.0"

            page.Items.Head.Description.Value.Length
            |> should equal PackageMetadata.limits.Description

            page.Items.Head.Tags |> should equal [ "one"; "two" ]

            feed.Requests
            |> should contain ("/query?q=Example&skip=0&take=1&prerelease=true&semVerLevel=2.0.0")
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``one malformed source does not clear successful results from another source``() =
        use good =
            new LocalFeed(fun root path ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root false)
                | "/query" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.searchResult "valid")
                | _ -> NuGetCatalogScenario.status 404)

        use malformed = new LocalFeed(fun _ _ -> NuGetCatalogScenario.response "{not-json")
        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Good", $"{good.Root}index.json"; "Malformed", $"{malformed.Root}index.json" ]
                []
                []

            let catalog = NuGetPackageCatalog.create ()

            let page =
                catalog.Search(NuGetCatalogScenario.searchRequest project None None 20)
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            page.Items |> should haveLength 1
            page.Items.Head.Source.Value |> should equal "Good"
            page.SourceFailures |> should haveLength 1

            PackageSourceFailure.kind page.SourceFailures.Head
            |> should equal PackageSourceFailureKind.Malformed
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``package details expose versions dependencies deprecation vulnerabilities and safe links``
        ()
        =
        use feed =
            new LocalFeed(fun root path ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root true)
                | "/registration/example.package/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.registration root)
                | _ -> NuGetCatalogScenario.status 404)

        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Local", $"{feed.Root}index.json" ]
                []
                []

            let catalog = NuGetPackageCatalog.create ()

            let details =
                catalog.Details(
                    NuGetCatalogScenario.request
                        project
                        { Package = NuGetCatalogScenario.packageId "Example.Package"
                          Version =
                            PackageVersionSelection.Exact(
                                NuGetVersion.create "2.0.0" |> Result.defaultWith (failwithf "%A")
                            )
                          Source = NuGetCatalogScenario.sourceId "Local" }
                )
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            details.Versions |> List.map _.Value |> should equal [ "2.0.0" ]
            details.Authors |> should equal [ "One Author"; "Two Author" ]
            details.ProjectUrl.Value.Host |> should equal "projects.example.test"
            details.License |> should equal (Some "MIT")
            details.LicenseUrl.Value.Host |> should equal "licenses.nuget.org"
            details.ReadmeUrl.Value.Host |> should equal "readmes.example.test"

            details.DependencyGroups
            |> Map.toList
            |> List.collect snd
            |> List.map (fun (package, range) -> package.Value, range.Value)
            |> should equal [ "Example.Dependency", "[1.2.0, 2.0.0)" ]

            match details.Deprecation with
            | PackageDeprecation.Deprecated(reasons, Some alternate) ->
                reasons |> NonEmptyList.toList |> should equal [ "Legacy" ]
                alternate.Identity.Value |> should equal "Replacement.Package"
                alternate.Range.Value.Value |> should equal "[3.0.0, )"
            | deprecation -> failwithf "Expected deprecation metadata, got %A" deprecation

            details.Vulnerabilities |> should haveLength 1

            details.Vulnerabilities.Head.Severity
            |> should equal PackageVulnerabilitySeverity.High

            details.Vulnerabilities.Head.Advisory.Host
            |> should equal "advisories.example.test"
        finally
            NuGetCatalogScenario.delete directory

    [<Theory>]
    [<InlineData(401, 0)>]
    [<InlineData(403, 1)>]
    member _.``credential failures are stable redacted and independent from selected state``
        (statusCode: int, expectedKind: int)
        =
        use feed = new LocalFeed(fun _ _ -> NuGetCatalogScenario.status statusCode)
        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Private", $"{feed.Root}index.json" ]
                []
                [ "Private", "sensitive-user", "sensitive-password" ]

            let catalog = NuGetPackageCatalog.create ()

            let page =
                catalog.Search(NuGetCatalogScenario.searchRequest project None None 20)
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            let sourceFailure = page.SourceFailures |> List.exactlyOne

            let expected =
                if expectedKind = 0 then
                    PackageSourceFailureKind.AuthenticationRequired
                else
                    PackageSourceFailureKind.Unauthorized

            PackageSourceFailure.kind sourceFailure |> should equal expected

            let exposed =
                sprintf "%A %s" sourceFailure (PackageSourceFailure.message sourceFailure)

            exposed |> should not' (haveSubstring "sensitive-user")
            exposed |> should not' (haveSubstring "sensitive-password")

            PackageSourceFailure.code sourceFailure
            |> should startWith "DWE-PACKAGE-SOURCE-"
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``unavailable source diagnostics omit dependency exception content``() =
        let source = NuGetCatalogScenario.sourceId "Offline"

        let failure =
            NuGetSourceFailures.sourceFailure
                source
                (HttpRequestException("private-host-and-query-must-not-escape"))

        PackageSourceFailure.kind failure
        |> should equal PackageSourceFailureKind.Unavailable

        PackageSourceFailure.code failure
        |> should equal "DWE-PACKAGE-SOURCE-UNAVAILABLE"

        PackageSourceFailure.message failure
        |> should not' (haveSubstring "private-host-and-query-must-not-escape")

    [<Fact>]
    member _.``selected-source failures preserve unauthorized and malformed classifications``() =
        let source = NuGetCatalogScenario.sourceId "Private"

        let packageFailure kind =
            PackageSourceFailure.create source kind
            |> NuGetSourceFailures.sourceFailureAsPackageFailure

        packageFailure PackageSourceFailureKind.Unauthorized
        |> PackageFailure.kind
        |> should equal PackageFailureKind.Unauthorized

        packageFailure PackageSourceFailureKind.Malformed
        |> PackageFailure.kind
        |> should equal PackageFailureKind.MalformedSource

    [<Fact>]
    member _.``active package search cancellation returns the stable cancelled failure``() =
        use feed =
            new LocalFeed(fun root path ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root false)
                | "/query" ->
                    NuGetCatalogScenario.delayed
                        (TimeSpan.FromSeconds 5.0)
                        (NuGetCatalogScenario.searchResult "slow")
                | _ -> NuGetCatalogScenario.status 404)

        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Slow", $"{feed.Root}index.json" ]
                []
                []

            let catalog = NuGetPackageCatalog.create ()

            let request = NuGetCatalogScenario.searchRequest project None None 20

            let running = Async.StartAsTask(catalog.Search request)
            Thread.Sleep 200

            catalog.Cancel(PackageCancellation.Request request.Id)
            |> NuGetCatalogScenario.run

            let failure = running.GetAwaiter().GetResult() |> NuGetCatalogScenario.failure
            PackageFailure.kind failure |> should equal PackageFailureKind.Cancelled
            PackageFailure.code failure |> should equal "DWE-PACKAGE-CANCELLED"
        finally
            NuGetCatalogScenario.delete directory
