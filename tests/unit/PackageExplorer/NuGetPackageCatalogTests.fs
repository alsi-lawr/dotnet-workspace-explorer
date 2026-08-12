namespace Dotnet.WorkspaceExplorer.PackageExplorer.UnitTests

open System
open System.Collections.Concurrent
open System.IO
open System.IO.Compression
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
      Content: byte array
      ContentType: string
      Delay: TimeSpan option }

type private LocalFeed(handler: string -> string -> string -> FeedResponse) =
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
                    let! context = listener.GetContextAsync().WaitAsync cancellation.Token

                    let path =
                        context.Request.Url
                        |> Option.ofObj
                        |> Option.map _.AbsolutePath
                        |> Option.defaultValue "/"

                    let rawUrl = context.Request.RawUrl |> Option.ofObj |> Option.defaultValue path

                    requests.Enqueue rawUrl
                    let response = handler root path rawUrl

                    match response.Delay with
                    | Some delay -> do! Task.Delay(delay, cancellation.Token)
                    | None -> ()

                    context.Response.StatusCode <- response.Status
                    context.Response.ContentType <- response.ContentType
                    context.Response.ContentLength64 <- response.Content.LongLength

                    do!
                        context.Response.OutputStream.WriteAsync(
                            response.Content,
                            cancellation.Token
                        )

                    context.Response.Close()
                with
                | :? OperationCanceledException -> ()
                | :? HttpListenerException when cancellation.IsCancellationRequested -> ()
                | :? IOException -> ()
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
    let response (content: string) =
        { Status = 200
          Content = Encoding.UTF8.GetBytes content
          ContentType = "application/json"
          Delay = None }

    let status code =
        { Status = code
          Content = Encoding.UTF8.GetBytes "{}"
          ContentType = "application/json"
          Delay = None }

    let delayed delay (content: string) =
        { Status = 200
          Content = Encoding.UTF8.GetBytes content
          ContentType = "application/json"
          Delay = Some delay }

    let package delay content =
        { Status = 200
          Content = content
          ContentType = "application/octet-stream"
          Delay = delay }

    let packageVersions = response """{"versions":["2.0.0"]}"""

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
                [ "RegistrationsBaseUrl"
                  "RegistrationsBaseUrl/3.0.0-beta"
                  "RegistrationsBaseUrl/3.0.0-rc"
                  "RegistrationsBaseUrl/3.6.0" ]
                |> List.map (fun resourceType ->
                    $"""{{"@id":"{root}registration/","@type":"{resourceType}"}}""")
            else
                []

        let search =
            [ "SearchQueryService"
              "SearchQueryService/3.0.0-beta"
              "SearchQueryService/3.0.0-rc"
              "SearchQueryService/3.5.0" ]
            |> List.map (fun resourceType ->
                $"""{{"@id":"{root}query","@type":"{resourceType}"}}""")

        let flat = $"""{{"@id":"{root}flat/","@type":"PackageBaseAddress/3.0.0"}}"""
        let resources = String.concat "," (search @ [ flat ] @ registration)
        $"""{{"version":"3.0.0","resources":[{resources}]}}"""

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

    let credentialBearingRegistration root =
        registration root
        |> fun document -> document.Replace("\"licenseExpression\": \"MIT\",", "")
        |> fun document ->
            document.Replace(
                "https://licenses.example.test/mit",
                "https://license-user:license-password@licenses.example.test/mit?sig=license-secret"
            )
        |> fun document ->
            document.Replace(
                "https://projects.example.test/package",
                "https://project-user:project-password@projects.example.test/package?sig=project-secret"
            )
        |> fun document ->
            document.Replace(
                "https://readmes.example.test/package",
                "https://readme-user:readme-password@readmes.example.test/package?sig=readme-secret"
            )
        |> fun document ->
            document.Replace(
                "https://advisories.example.test/one",
                "https://advisory-user:advisory-password@advisories.example.test/one?sig=advisory-secret"
            )

    let packageArchive readme =
        use output = new MemoryStream()

        do
            use archive = new ZipArchive(output, ZipArchiveMode.Create, true)

            let writeEntry path (content: string) =
                let entry = archive.CreateEntry path
                use writer = new StreamWriter(entry.Open(), UTF8Encoding false)
                writer.Write content

            let readmeElement =
                readme
                |> Option.map (fun (path, _) -> $"<readme>{path}</readme>")
                |> Option.defaultValue ""

            writeEntry
                "Example.Package.nuspec"
                $"""
                <package>
                    <metadata>
                        <id>Example.Package</id>
                        <version>2.0.0</version>
                        {readmeElement}
                    </metadata>
                </package>
                """

            readme |> Option.iter (fun (path, content) -> writeEntry path content)

        output.ToArray()

    let searchResults packages =
        let data =
            packages
            |> List.map (fun (identity, description) ->
                $"""
                {{
                    "id": "{identity}",
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
                """)
            |> String.concat ","

        $"""{{"totalHits":{packages.Length},"data":[{data}]}}"""

    let searchResult description =
        searchResults [ "Example.Package", description ]

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
                  Source = source
                  Order = PackageSearchOrder.Relevance }
              PageSize = pageSize size
              Continuation = continuation }

    let search (catalog: PackageCatalogPorts) request =
        async {
            let! outcome = PackageProducer.collect catalog.Search request

            return
                outcome
                |> Result.map (fun (items, completion) ->
                    { Items = items
                      Continuation = completion.Continuation
                      SourceFailures = completion.SourceFailures })
        }

    let detailsRequest project source =
        request
            project
            { Package = packageId "Example.Package"
              Version =
                PackageVersionSelection.Exact(
                    NuGetVersion.create "2.0.0" |> Result.defaultWith (failwithf "%A")
                )
              Source = sourceId source }

    let waitForRequest (feed: LocalFeed) expected =
        let timeout = DateTime.UtcNow + TimeSpan.FromSeconds 5.0

        while DateTime.UtcNow < timeout
              && not (feed.Requests |> List.exists (fun request -> request = expected)) do
            Thread.Sleep 10

        feed.Requests |> should contain expected

    let run operation = Async.RunSynchronously operation

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
                [ "First",
                  "http://source-user:source-password@127.0.0.1:40101/index.json?sig=source-query-secret"
                  "Second", "http://127.0.0.1:40102/index.json" ]
                []
                [ "First", "private-user", "do-not-return-this-secret" ]

            let catalog = NuGetPackageCatalog.create ()

            let sources =
                catalog.ConfiguredSources(NuGetCatalogScenario.request project ())
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            sources |> List.map _.Name |> should equal [ "First"; "Second" ]
            sources.Head.Location.UserInfo |> should equal ""
            sources.Head.Location.Query |> should equal ""

            let exposed = sprintf "%A" sources
            exposed |> should not' (haveSubstring "private-user")
            exposed |> should not' (haveSubstring "do-not-return-this-secret")
            exposed |> should not' (haveSubstring "source-user")
            exposed |> should not' (haveSubstring "source-password")
            exposed |> should not' (haveSubstring "source-query-secret")
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``redacted source location keeps the raw query private for NuGet operations``() =
        use feed =
            new LocalFeed(fun root path _ ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root false)
                | "/query" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.searchResult "available")
                | _ -> NuGetCatalogScenario.status 404)

        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "PrivateQuery", $"{feed.Root}index.json?sig=raw-source-secret" ]
                []
                []

            let catalog = NuGetPackageCatalog.create ()

            let source =
                catalog.ConfiguredSources(NuGetCatalogScenario.request project ())
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value
                |> List.exactlyOne

            source.Location.Query |> should equal ""

            let page =
                NuGetCatalogScenario.search
                    catalog
                    (NuGetCatalogScenario.searchRequest project (Some source.Id) None 1)
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            page.Items |> should haveLength 1
            feed.Requests |> should contain "/index.json?sig=raw-source-secret"
            sprintf "%A %A" source page |> should not' (haveSubstring "raw-source-secret")
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
            let fallback = NuGetCatalogScenario.sourceId "Fallback"

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

            evaluate (Some contoso) (Some [ NuGetCatalogScenario.packageId "Other.Dependency" ])
            |> should equal (PackageSourceMappingPolicy.Allowed [ contoso ])

            catalog.SourceMapping(
                NuGetCatalogScenario.request
                    project
                    { Package = NuGetCatalogScenario.packageId "Other.Dependency"
                      CandidateSource = Some fallback
                      RestoredTransitives = Some [] }
            )
            |> NuGetCatalogScenario.run
            |> NuGetCatalogScenario.value
            |> should equal (PackageSourceMappingPolicy.Allowed [ fallback ])

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
            new LocalFeed(fun root path _ ->
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
                NuGetCatalogScenario.search
                    catalog
                    (NuGetCatalogScenario.searchRequest project None None 1)
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
            |> should contain "/query?q=Example&skip=0&take=1&prerelease=true&semVerLevel=2.0.0"
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``size-one continuation preserves ordered items across two configured sources``() =
        let sourceFeed identity =
            new LocalFeed(fun root path rawUrl ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root false)
                | "/query" when rawUrl.Contains("skip=0", StringComparison.Ordinal) ->
                    NuGetCatalogScenario.response (
                        NuGetCatalogScenario.searchResults [ identity, "result" ]
                    )
                | "/query" -> NuGetCatalogScenario.response (NuGetCatalogScenario.searchResults [])
                | _ -> NuGetCatalogScenario.status 404)

        use first = sourceFeed "First.Package"
        use second = sourceFeed "Second.Package"
        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "First", $"{first.Root}index.json"; "Second", $"{second.Root}index.json" ]
                []
                []

            let catalog = NuGetPackageCatalog.create ()

            let page continuation =
                NuGetCatalogScenario.search
                    catalog
                    (NuGetCatalogScenario.searchRequest project None continuation 1)
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            let firstPage = page None
            let secondPage = page firstPage.Continuation
            let finalPage = page secondPage.Continuation

            [ firstPage; secondPage; finalPage ]
            |> List.iter (fun result -> result.Items.Length |> should be (lessThanOrEqualTo 1))

            firstPage.Items |> List.map _.Identity.Value |> should equal [ "First.Package" ]

            secondPage.Items
            |> List.map _.Identity.Value
            |> should equal [ "Second.Package" ]

            finalPage.Items |> should be Empty
            finalPage.Continuation |> should equal None
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``search exposes the first source batch before terminal metadata and preserves duplicates``
        ()
        =
        let sourceFeed () =
            new LocalFeed(fun root path rawUrl ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root false)
                | "/query" when rawUrl.Contains("skip=0", StringComparison.Ordinal) ->
                    NuGetCatalogScenario.response (
                        NuGetCatalogScenario.searchResults [ "Shared.Package", "result" ]
                    )
                | "/query" -> NuGetCatalogScenario.response (NuGetCatalogScenario.searchResults [])
                | _ -> NuGetCatalogScenario.status 404)

        use first = sourceFeed ()
        use second = sourceFeed ()
        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "First", $"{first.Root}index.json"; "Second", $"{second.Root}index.json" ]
                []
                []

            let catalog = NuGetPackageCatalog.create ()
            let request = NuGetCatalogScenario.searchRequest project None None 2
            let batches = ResizeArray<PackageSummary list>()

            let firstBatch =
                TaskCompletionSource TaskCreationOptions.RunContinuationsAsynchronously

            let release =
                TaskCompletionSource TaskCreationOptions.RunContinuationsAsynchronously

            let sink _ batch =
                async {
                    batches.Add(NonEmptyList.toList batch)

                    if batches.Count = 1 then
                        firstBatch.TrySetResult() |> ignore
                        do! release.Task |> Async.AwaitTask
                }

            let running = catalog.Search request sink |> Async.StartAsTask
            firstBatch.Task.Wait(TimeSpan.FromSeconds 5.0) |> should equal true
            running.IsCompleted |> should equal false
            release.TrySetResult() |> ignore

            let completion = running.GetAwaiter().GetResult() |> NuGetCatalogScenario.value

            batches |> Seq.toList |> should haveLength 2

            batches
            |> Seq.collect id
            |> Seq.map _.Identity.Value
            |> Seq.toList
            |> should equal [ "Shared.Package"; "Shared.Package" ]

            completion.Query |> should equal request.Value.Search
            completion.Continuation.IsSome |> should equal true
            completion.SourceFailures |> should be Empty
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``continuation preserves overflow when a feed ignores the requested take``() =
        let packages =
            [ "First.Package", "first"
              "Second.Package", "second"
              "Third.Package", "third" ]

        use feed =
            new LocalFeed(fun root path rawUrl ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root false)
                | "/query" ->
                    let remaining =
                        if rawUrl.Contains("skip=1", StringComparison.Ordinal) then
                            packages |> List.skip 1
                        elif rawUrl.Contains("skip=2", StringComparison.Ordinal) then
                            packages |> List.skip 2
                        elif rawUrl.Contains("skip=3", StringComparison.Ordinal) then
                            []
                        else
                            packages

                    NuGetCatalogScenario.response (NuGetCatalogScenario.searchResults remaining)
                | _ -> NuGetCatalogScenario.status 404)

        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Nonconforming", $"{feed.Root}index.json" ]
                []
                []

            let catalog = NuGetPackageCatalog.create ()

            let page continuation =
                NuGetCatalogScenario.search
                    catalog
                    (NuGetCatalogScenario.searchRequest project None continuation 1)
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            let firstPage = page None
            let secondPage = page firstPage.Continuation
            let thirdPage = page secondPage.Continuation
            let finalPage = page thirdPage.Continuation

            [ firstPage; secondPage; thirdPage; finalPage ]
            |> List.iter (fun result -> result.Items.Length |> should be (lessThanOrEqualTo 1))

            [ firstPage; secondPage; thirdPage ]
            |> List.collect _.Items
            |> List.map _.Identity.Value
            |> should equal [ "First.Package"; "Second.Package"; "Third.Package" ]

            finalPage.Items |> should be Empty
            finalPage.Continuation |> should equal None
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``size-one continuation advances past a failed source without losing later items``() =
        use unavailable =
            new LocalFeed(fun _ path _ ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response """{"version":"3.0.0","resources":[]}"""
                | _ -> NuGetCatalogScenario.status 404)

        let packages = [ "First.Healthy", "first"; "Second.Healthy", "second" ]

        use healthy =
            new LocalFeed(fun root path rawUrl ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root false)
                | "/query" ->
                    let remaining =
                        if rawUrl.Contains("skip=1", StringComparison.Ordinal) then
                            packages |> List.skip 1
                        elif rawUrl.Contains("skip=2", StringComparison.Ordinal) then
                            []
                        else
                            packages

                    NuGetCatalogScenario.response (NuGetCatalogScenario.searchResults remaining)
                | _ -> NuGetCatalogScenario.status 404)

        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Unavailable", $"{unavailable.Root}index.json"
                  "Healthy", $"{healthy.Root}index.json" ]
                []
                []

            let catalog = NuGetPackageCatalog.create ()

            let page continuation =
                NuGetCatalogScenario.search
                    catalog
                    (NuGetCatalogScenario.searchRequest project None continuation 1)
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            let firstPage = page None
            let secondPage = page firstPage.Continuation
            let finalPage = page secondPage.Continuation

            [ firstPage; secondPage; finalPage ]
            |> List.iter (fun result -> result.Items.Length |> should be (lessThanOrEqualTo 1))

            [ firstPage; secondPage ]
            |> List.collect _.Items
            |> List.map _.Identity.Value
            |> should equal [ "First.Healthy"; "Second.Healthy" ]

            firstPage.SourceFailures
            |> List.map PackageSourceFailure.source
            |> should equal [ NuGetCatalogScenario.sourceId "Unavailable" ]

            finalPage.Items |> should be Empty
            finalPage.Continuation |> should equal None
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``one malformed source does not clear successful results from another source``() =
        use good =
            new LocalFeed(fun root path _ ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root false)
                | "/query" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.searchResult "valid")
                | _ -> NuGetCatalogScenario.status 404)

        use malformed =
            new LocalFeed(fun _ _ _ -> NuGetCatalogScenario.response "{not-json")

        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Good", $"{good.Root}index.json"; "Malformed", $"{malformed.Root}index.json" ]
                []
                []

            let catalog = NuGetPackageCatalog.create ()

            let page =
                NuGetCatalogScenario.search
                    catalog
                    (NuGetCatalogScenario.searchRequest project None None 20)
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
        let commonMark =
            """
# Example Package

Use `Example.Package` as-is.
"""

        use feed =
            new LocalFeed(fun root path _ ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root true)
                | "/registration/example.package/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.registration root)
                | "/flat/example.package/index.json" -> NuGetCatalogScenario.packageVersions
                | "/flat/example.package/2.0.0/example.package.2.0.0.nupkg" ->
                    NuGetCatalogScenario.package
                        None
                        (NuGetCatalogScenario.packageArchive (Some("docs/README.md", commonMark)))
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
            details.ReadmeContent |> should equal (Some commonMark)

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

    [<Fact>]
    member _.``selected package source returns unchanged README CommonMark content``() =
        let feedWith readme =
            new LocalFeed(fun root path _ ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root true)
                | "/registration/example.package/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.registration root)
                | "/flat/example.package/index.json" -> NuGetCatalogScenario.packageVersions
                | "/flat/example.package/2.0.0/example.package.2.0.0.nupkg" ->
                    NuGetCatalogScenario.package
                        None
                        (NuGetCatalogScenario.packageArchive (Some("docs/README.md", readme)))
                | _ -> NuGetCatalogScenario.status 404)

        use first = feedWith "# README from first"
        use selected = feedWith "# README from selected\n\n- unchanged\n"
        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "First", $"{first.Root}index.json"; "Selected", $"{selected.Root}index.json" ]
                []
                []

            let details =
                NuGetPackageCatalog.create ()
                |> fun catalog ->
                    catalog.Details(NuGetCatalogScenario.detailsRequest project "Selected")
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            details.ReadmeContent
            |> should equal (Some "# README from selected\n\n- unchanged\n")

            first.Requests
            |> should not' (contain "/flat/example.package/2.0.0/example.package.2.0.0.nupkg")

            selected.Requests
            |> should contain "/flat/example.package/2.0.0/example.package.2.0.0.nupkg"
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``package details return no README when the selected package manifest declares none``
        ()
        =
        use feed =
            new LocalFeed(fun root path _ ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root true)
                | "/registration/example.package/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.registration root)
                | "/flat/example.package/index.json" -> NuGetCatalogScenario.packageVersions
                | "/flat/example.package/2.0.0/example.package.2.0.0.nupkg" ->
                    NuGetCatalogScenario.package None (NuGetCatalogScenario.packageArchive None)
                | _ -> NuGetCatalogScenario.status 404)

        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Local", $"{feed.Root}index.json" ]
                []
                []

            let details =
                NuGetPackageCatalog.create ()
                |> fun catalog ->
                    catalog.Details(NuGetCatalogScenario.detailsRequest project "Local")
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            details.ReadmeContent |> should equal None
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``package details reject a README path that escapes the package archive``() =
        use feed =
            new LocalFeed(fun root path _ ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root true)
                | "/registration/example.package/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.registration root)
                | "/flat/example.package/index.json" -> NuGetCatalogScenario.packageVersions
                | "/flat/example.package/2.0.0/example.package.2.0.0.nupkg" ->
                    NuGetCatalogScenario.package
                        None
                        (NuGetCatalogScenario.packageArchive (
                            Some("../README.md", "# Unsafe README")
                        ))
                | _ -> NuGetCatalogScenario.status 404)

        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Local", $"{feed.Root}index.json" ]
                []
                []

            let failure =
                NuGetPackageCatalog.create ()
                |> fun catalog ->
                    catalog.Details(NuGetCatalogScenario.detailsRequest project "Local")
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.failure

            PackageFailure.kind failure |> should equal PackageFailureKind.MalformedSource
            PackageFailure.code failure |> should equal "DWE-PACKAGE-SOURCE-MALFORMED"
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``unavailable package README downloads retain a stable redacted package failure``() =
        use feed =
            new LocalFeed(fun root path _ ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root true)
                | "/registration/example.package/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.registration root)
                | "/flat/example.package/index.json" -> NuGetCatalogScenario.packageVersions
                | "/flat/example.package/2.0.0/example.package.2.0.0.nupkg" ->
                    NuGetCatalogScenario.status 500
                | _ -> NuGetCatalogScenario.status 404)

        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Private", $"{feed.Root}index.json?sig=readme-source-secret" ]
                []
                []

            let failure =
                NuGetPackageCatalog.create ()
                |> fun catalog ->
                    catalog.Details(NuGetCatalogScenario.detailsRequest project "Private")
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.failure

            PackageFailure.kind failure |> should equal PackageFailureKind.SourceUnavailable

            let exposed = sprintf "%A %s" failure (PackageFailure.message failure)
            exposed |> should not' (haveSubstring "readme-source-secret")
        finally
            NuGetCatalogScenario.delete directory

    [<Theory>]
    [<InlineData(401, 0)>]
    [<InlineData(403, 1)>]
    member _.``archive-only credential failures retain stable redacted package classifications``
        (statusCode: int, expectedKind: int)
        =
        use feed =
            new LocalFeed(fun root path _ ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root true)
                | "/registration/example.package/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.registration root)
                | "/flat/example.package/index.json" -> NuGetCatalogScenario.packageVersions
                | "/flat/example.package/2.0.0/example.package.2.0.0.nupkg" ->
                    NuGetCatalogScenario.status statusCode
                | _ -> NuGetCatalogScenario.status 404)

        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Private", $"{feed.Root}index.json?sig=archive-source-secret" ]
                []
                [ "Private", "archive-user", "archive-password" ]

            let failure =
                NuGetPackageCatalog.create ()
                |> fun catalog ->
                    catalog.Details(NuGetCatalogScenario.detailsRequest project "Private")
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.failure

            let expected =
                if expectedKind = 0 then
                    PackageFailureKind.AuthenticationRequired
                else
                    PackageFailureKind.Unauthorized

            PackageFailure.kind failure |> should equal expected

            let exposed = sprintf "%A %s" failure (PackageFailure.message failure)
            exposed |> should not' (haveSubstring "archive-user")
            exposed |> should not' (haveSubstring "archive-password")
            exposed |> should not' (haveSubstring "archive-source-secret")
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``package details preserve credential-required failures without exposing credentials``
        ()
        =
        use feed = new LocalFeed(fun _ _ _ -> NuGetCatalogScenario.status 401)
        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Private", $"{feed.Root}index.json?sig=readme-source-secret" ]
                []
                [ "Private", "readme-user", "readme-password" ]

            let failure =
                NuGetPackageCatalog.create ()
                |> fun catalog ->
                    catalog.Details(NuGetCatalogScenario.detailsRequest project "Private")
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.failure

            PackageFailure.kind failure
            |> should equal PackageFailureKind.AuthenticationRequired

            let exposed = sprintf "%A %s" failure (PackageFailure.message failure)
            exposed |> should not' (haveSubstring "readme-user")
            exposed |> should not' (haveSubstring "readme-password")
            exposed |> should not' (haveSubstring "readme-source-secret")
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``credential-bearing metadata links and advisories never enter package details``() =
        use feed =
            new LocalFeed(fun root path _ ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root true)
                | "/registration/example.package/index.json" ->
                    NuGetCatalogScenario.response (
                        NuGetCatalogScenario.credentialBearingRegistration root
                    )
                | "/flat/example.package/index.json" -> NuGetCatalogScenario.packageVersions
                | "/flat/example.package/2.0.0/example.package.2.0.0.nupkg" ->
                    NuGetCatalogScenario.package None (NuGetCatalogScenario.packageArchive None)
                | _ -> NuGetCatalogScenario.status 404)

        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "PrivateMetadata", $"{feed.Root}index.json" ]
                []
                []

            let details =
                NuGetPackageCatalog.create ()
                |> fun catalog ->
                    catalog.Details(
                        NuGetCatalogScenario.request
                            project
                            { Package = NuGetCatalogScenario.packageId "Example.Package"
                              Version =
                                PackageVersionSelection.Exact(
                                    NuGetVersion.create "2.0.0"
                                    |> Result.defaultWith (failwithf "%A")
                                )
                              Source = NuGetCatalogScenario.sourceId "PrivateMetadata" }
                    )
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            details.ProjectUrl |> should equal None
            details.License |> should equal None
            details.LicenseUrl |> should equal None
            details.ReadmeUrl |> should equal None
            details.ReadmeContent |> should equal None
            details.Vulnerabilities |> should be Empty

            let exposed = sprintf "%A" details

            for secret in
                [ "project-user"
                  "project-secret"
                  "license-password"
                  "license-secret"
                  "readme-user"
                  "readme-secret"
                  "advisory-password"
                  "advisory-secret" ] do
                exposed |> should not' (haveSubstring secret)
        finally
            NuGetCatalogScenario.delete directory

    [<Theory>]
    [<InlineData(401, 0)>]
    [<InlineData(403, 1)>]
    member _.``credential failures are stable redacted and independent from selected state``
        (statusCode: int, expectedKind: int)
        =
        use feed = new LocalFeed(fun _ _ _ -> NuGetCatalogScenario.status statusCode)
        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Private", $"{feed.Root}index.json?sig=diagnostic-query-secret" ]
                []
                [ "Private", "sensitive-user", "sensitive-password" ]

            let catalog = NuGetPackageCatalog.create ()

            let page =
                NuGetCatalogScenario.search
                    catalog
                    (NuGetCatalogScenario.searchRequest project None None 20)
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
            exposed |> should not' (haveSubstring "diagnostic-query-secret")

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
                (HttpRequestException
                    "https://diag-user:diag-password@private.test/index.json?sig=diag-secret")

        PackageSourceFailure.kind failure
        |> should equal PackageSourceFailureKind.Unavailable

        PackageSourceFailure.code failure
        |> should equal "DWE-PACKAGE-SOURCE-UNAVAILABLE"

        PackageSourceFailure.message failure |> should not' (haveSubstring "diag-user")

        PackageSourceFailure.message failure
        |> should not' (haveSubstring "diag-password")

        PackageSourceFailure.message failure
        |> should not' (haveSubstring "diag-secret")

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
    member _.``oversized remote collections stop at documented model-entry bounds``() =
        let oversized maximum = seq { 1 .. maximum + 25 }

        oversized PackageMetadata.limits.AvailableVersions
        |> PackageMetadata.availableVersions
        |> Seq.length
        |> should equal PackageMetadata.limits.AvailableVersions

        oversized PackageMetadata.limits.DependencyGroups
        |> PackageMetadata.dependencyGroups
        |> Seq.length
        |> should equal PackageMetadata.limits.DependencyGroups

        oversized PackageMetadata.limits.DependenciesPerGroup
        |> PackageMetadata.dependencies
        |> Seq.length
        |> should equal PackageMetadata.limits.DependenciesPerGroup

        PackageMetadata.mergeDependencies
            [ 1 .. PackageMetadata.limits.DependenciesPerGroup ]
            [ PackageMetadata.limits.DependenciesPerGroup + 1 ]
        |> List.length
        |> should equal PackageMetadata.limits.DependenciesPerGroup

    [<Fact>]
    member _.``active package search cancellation returns the stable cancelled failure``() =
        use feed =
            new LocalFeed(fun root path _ ->
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

            let running = Async.StartAsTask(NuGetCatalogScenario.search catalog request)
            Thread.Sleep 200

            catalog.Cancel(PackageCancellation.Request request.Id)
            |> NuGetCatalogScenario.run

            let failure = running.GetAwaiter().GetResult() |> NuGetCatalogScenario.failure
            PackageFailure.kind failure |> should equal PackageFailureKind.Cancelled
            PackageFailure.code failure |> should equal "DWE-PACKAGE-CANCELLED"
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``active package README download cancellation returns no late package details``() =
        let packagePath = "/flat/example.package/2.0.0/example.package.2.0.0.nupkg"

        use feed =
            new LocalFeed(fun root path _ ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root true)
                | "/registration/example.package/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.registration root)
                | "/flat/example.package/index.json" -> NuGetCatalogScenario.packageVersions
                | path when path = packagePath ->
                    NuGetCatalogScenario.package
                        (Some(TimeSpan.FromSeconds 5.0))
                        (NuGetCatalogScenario.packageArchive (Some("README.md", "# Late README")))
                | _ -> NuGetCatalogScenario.status 404)

        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Slow", $"{feed.Root}index.json" ]
                []
                []

            let catalog = NuGetPackageCatalog.create ()
            let request = NuGetCatalogScenario.detailsRequest project "Slow"
            let running = Async.StartAsTask(catalog.Details request)
            NuGetCatalogScenario.waitForRequest feed packagePath

            catalog.Cancel(PackageCancellation.Request request.Id)
            |> NuGetCatalogScenario.run

            let failure = running.GetAwaiter().GetResult() |> NuGetCatalogScenario.failure
            PackageFailure.kind failure |> should equal PackageFailureKind.Cancelled
            PackageFailure.code failure |> should equal "DWE-PACKAGE-CANCELLED"
        finally
            NuGetCatalogScenario.delete directory

    [<Fact>]
    member _.``replacement package details return only their README after cancelled work finishes``
        ()
        =
        let packagePath = "/flat/example.package/2.0.0/example.package.2.0.0.nupkg"
        let mutable downloads = 0

        use feed =
            new LocalFeed(fun root path _ ->
                match path with
                | "/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.serviceIndex root true)
                | "/registration/example.package/index.json" ->
                    NuGetCatalogScenario.response (NuGetCatalogScenario.registration root)
                | "/flat/example.package/index.json" -> NuGetCatalogScenario.packageVersions
                | path when path = packagePath ->
                    let download = Interlocked.Increment(&downloads)

                    if download = 1 then
                        NuGetCatalogScenario.package
                            (Some(TimeSpan.FromMilliseconds 250.0))
                            (NuGetCatalogScenario.packageArchive (
                                Some("README.md", "# Superseded README")
                            ))
                    else
                        NuGetCatalogScenario.package
                            None
                            (NuGetCatalogScenario.packageArchive (
                                Some("README.md", "# Current README")
                            ))
                | _ -> NuGetCatalogScenario.status 404)

        let directory, project = NuGetCatalogScenario.temporaryWorkspace ()

        try
            NuGetCatalogScenario.writeConfiguration
                directory
                [ "Local", $"{feed.Root}index.json" ]
                []
                []

            let catalog = NuGetPackageCatalog.create ()
            let superseded = NuGetCatalogScenario.detailsRequest project "Local"
            let running = Async.StartAsTask(catalog.Details superseded)
            NuGetCatalogScenario.waitForRequest feed packagePath

            catalog.Cancel(PackageCancellation.Request superseded.Id)
            |> NuGetCatalogScenario.run

            running.GetAwaiter().GetResult()
            |> NuGetCatalogScenario.failure
            |> PackageFailure.kind
            |> should equal PackageFailureKind.Cancelled

            let current =
                catalog.Details(NuGetCatalogScenario.detailsRequest project "Local")
                |> NuGetCatalogScenario.run
                |> NuGetCatalogScenario.value

            current.ReadmeContent |> should equal (Some "# Current README")
        finally
            NuGetCatalogScenario.delete directory
