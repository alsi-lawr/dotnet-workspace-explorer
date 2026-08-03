namespace Dotnet.WorkspaceExplorer.PackageExplorer

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Threading
open System.Xml
open System.Xml.Linq
open Dotnet.WorkspaceExplorer.Packages
open NuGet.Common
open NuGet.Packaging.Core
open NuGet.Protocol
open NuGet.Protocol.Core.Types
open NuGet.Versioning

[<RequireQualifiedAccess>]
module internal NuGetPackageReadme =
    let private logger = NullLogger.Instance

    let private malformed message = raise (InvalidDataException message)

    let private manifestEntry (archive: ZipArchive) =
        let manifests =
            archive.Entries
            |> Seq.filter (fun entry ->
                not (String.IsNullOrEmpty entry.Name)
                && not (entry.FullName.Contains '/')
                && not (entry.FullName.Contains '\\')
                && entry.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            |> Seq.toList

        match manifests with
        | [ manifest ] -> manifest
        | _ -> malformed "The package archive must contain one root package manifest."

    let private declaredReadmePath (manifest: ZipArchiveEntry) =
        use stream = manifest.Open()

        let settings = XmlReaderSettings()
        settings.DtdProcessing <- DtdProcessing.Prohibit
        settings.XmlResolver <- null

        use reader = XmlReader.Create(stream, settings)
        let document = XDocument.Load reader

        document.Descendants()
        |> Seq.tryFind (fun element ->
            String.Equals(element.Name.LocalName, "readme", StringComparison.OrdinalIgnoreCase))
        |> Option.map _.Value
        |> Option.bind (fun value ->
            let path = value.Trim().Replace('\\', '/')

            if String.IsNullOrEmpty path then None else Some path)

    let private safeReadmeEntry (archive: ZipArchive) (path: string) =
        let segments = path.Split '/'

        if
            path.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathRooted path
            || segments
               |> Array.exists (fun segment ->
                   String.IsNullOrEmpty segment || segment = "." || segment = "..")
        then
            malformed "The package manifest declares an invalid README path."

        let matches =
            archive.Entries
            |> Seq.filter (fun entry ->
                not (String.IsNullOrEmpty entry.Name)
                && String.Equals(
                    entry.FullName.Replace('\\', '/'),
                    path,
                    StringComparison.OrdinalIgnoreCase
                ))
            |> Seq.toList

        match matches with
        | [ entry ] -> entry
        | _ -> malformed "The package manifest README entry is missing or ambiguous."

    let private readUtf8 (token: CancellationToken) (entry: ZipArchiveEntry) =
        async {
            token.ThrowIfCancellationRequested()
            use stream = entry.Open()
            use reader = new StreamReader(stream, UTF8Encoding(false, true), false)
            let! content = reader.ReadToEndAsync token |> Async.AwaitTask
            token.ThrowIfCancellationRequested()
            return content
        }

    let private extractReadme (token: CancellationToken) (package: MemoryStream) =
        async {
            token.ThrowIfCancellationRequested()
            package.Position <- 0L
            use archive = new ZipArchive(package, ZipArchiveMode.Read, true)
            let manifest = manifestEntry archive

            match declaredReadmePath manifest with
            | None -> return None
            | Some path ->
                let entry = safeReadmeEntry archive path
                let! content = readUtf8 token entry
                return Some content
        }

    let private unavailable source =
        PackageSourceFailure.create source.Model.Id PackageSourceFailureKind.Unavailable
        |> Error

    let private retryDownload
        (source: ConfiguredSource)
        (cache: SourceCacheContext)
        (identity: PackageId)
        (version: NuGetVersion)
        (token: CancellationToken)
        (package: MemoryStream)
        =
        async {
            let! resource =
                source.Repository.GetResourceAsync<DownloadResource> token |> Async.AwaitTask

            if isNull resource then
                return unavailable source
            else
                let temporaryDirectory =
                    Path.Combine(Path.GetTempPath(), $"dotnet-we-package-readme-{Guid.NewGuid():N}")

                Directory.CreateDirectory temporaryDirectory |> ignore

                let context = PackageDownloadContext(cache, temporaryDirectory, true)

                try
                    let packageIdentity = PackageIdentity(identity.Value, version)

                    use! result =
                        resource.GetDownloadResourceResultAsync(
                            packageIdentity,
                            context,
                            temporaryDirectory,
                            logger,
                            token
                        )
                        |> Async.AwaitTask

                    match result.Status with
                    | DownloadResourceResultStatus.Available when not (isNull result.PackageStream) ->
                        package.SetLength 0L

                        do! result.PackageStream.CopyToAsync(package, token) |> Async.AwaitTask

                        return Ok()
                    | DownloadResourceResultStatus.Cancelled ->
                        return raise (OperationCanceledException token)
                    | _ -> return unavailable source
                finally
                    GetDownloadResultUtility.CleanUpDirectDownloads context
                    Directory.Delete(temporaryDirectory, true)
        }

    let read
        (source: ConfiguredSource)
        (cache: SourceCacheContext)
        (identity: PackageId)
        (version: NuGetVersion)
        (token: CancellationToken)
        =
        async {
            try
                let! resource =
                    source.Repository.GetResourceAsync<FindPackageByIdResource> token
                    |> Async.AwaitTask

                if isNull resource then
                    return unavailable source
                else
                    use package = new MemoryStream()

                    let! copied =
                        resource.CopyNupkgToStreamAsync(
                            identity.Value,
                            version,
                            package,
                            cache,
                            logger,
                            token
                        )
                        |> Async.AwaitTask

                    token.ThrowIfCancellationRequested()

                    let! available =
                        if copied then
                            async { return Ok() }
                        else
                            retryDownload source cache identity version token package

                    match available with
                    | Error failure -> return Error failure
                    | Ok() ->
                        let! readme = extractReadme token package
                        token.ThrowIfCancellationRequested()
                        return Ok readme
            with
            | _ when token.IsCancellationRequested ->
                return raise (OperationCanceledException token)
            | error -> return Error(NuGetSourceFailures.sourceFailure source.Model.Id error)
        }
