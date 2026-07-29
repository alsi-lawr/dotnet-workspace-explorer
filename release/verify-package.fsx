open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Text
open System.Xml.Linq

let fail message =
    raise (InvalidOperationException message)

let require condition message =
    if not condition then
        fail message

let repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let configuration =
    match fsi.CommandLineArgs |> Array.skip 1 |> Array.toList with
    | [ "--configuration"; "Release" ] -> "Release"
    | _ -> fail "Usage: dotnet fsi release/verify-package.fsx --configuration Release"

let run directory executable arguments =
    let start = ProcessStartInfo executable
    start.WorkingDirectory <- directory
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.CreateNoWindow <- true

    for argument in arguments do
        start.ArgumentList.Add argument

    use child = Process.Start start
    require (not (isNull child)) $"Could not start {executable}."
    let output = child.StandardOutput.ReadToEndAsync()
    let error = child.StandardError.ReadToEndAsync()
    child.WaitForExit()

    require
        (child.ExitCode = 0)
        $"{executable} failed ({child.ExitCode}).\nstdout:\n{output.Result}\nstderr:\n{error.Result}"

    output.Result, error.Result

let readEntry (entry: ZipArchiveEntry) =
    use stream = entry.Open()
    use reader = new StreamReader(stream, Encoding.UTF8, true)
    reader.ReadToEnd()

let inspectPackage packagePath packageId version =
    use archive = ZipFile.OpenRead packagePath

    let entries =
        archive.Entries
        |> Seq.map (fun entry -> entry.FullName.Replace('\\', '/').ToLowerInvariant())
        |> Set.ofSeq

    for path in
        [ "readme.md"
          "tools/net10.0/any/dotnettoolsettings.xml"
          "tools/net10.0/any/dotnet.cli.plus.dll"
          "tools/net10.0/any/dotnet.cli.plus.deps.json"
          "tools/net10.0/any/dotnet.cli.plus.runtimeconfig.json" ] do
        require (entries.Contains path) $"The package is missing essential tool content: {path}"

    let nuspec =
        archive.Entries
        |> Seq.tryFind (fun entry ->
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
        |> Option.defaultWith (fun () -> fail "The package does not contain a nuspec.")
        |> readEntry
        |> XDocument.Parse

    let element name =
        nuspec.Descendants()
        |> Seq.tryFind (fun item -> item.Name.LocalName = name)
        |> Option.map _.Value

    require (element "id" = Some packageId) "The nuspec package ID changed."
    require (element "version" = Some version) "The nuspec package version changed."
    require (element "readme" = Some "README.md") "The package no longer declares its README."

    let isDotnetTool =
        nuspec.Descendants()
        |> Seq.filter (fun item -> item.Name.LocalName = "packageType")
        |> Seq.collect _.Attributes()
        |> Seq.exists (fun attribute ->
            attribute.Name.LocalName = "name"
            && attribute.Value.Equals("DotnetTool", StringComparison.OrdinalIgnoreCase))

    require isDotnetTool "The package is not marked as a .NET tool."

let runRoot =
    Path.Combine(repositoryRoot, ".agent-workspace", "release", $"package-{Guid.NewGuid():N}")

let packageDirectory = Path.Combine(runRoot, "package")
let toolDirectory = Path.Combine(runRoot, "tool")
let fixtureDirectory = Path.Combine(runRoot, "fixture")
let nugetConfig = Path.Combine(runRoot, "NuGet.Config")
let packageId = "Dotnet.CLI.Plus"
let version = $"1.0.0-smoke.{Guid.NewGuid():N}"

try
    Directory.CreateDirectory packageDirectory |> ignore
    Directory.CreateDirectory toolDirectory |> ignore
    Directory.CreateDirectory fixtureDirectory |> ignore

    File.WriteAllText(
        nugetConfig,
        "<configuration><packageSources><clear /></packageSources></configuration>"
    )

    let project =
        Path.Combine(repositoryRoot, "src", "Dotnet.CLI.Plus", "Dotnet.CLI.Plus.fsproj")

    run
        repositoryRoot
        "dotnet"
        [ "pack"
          project
          "--configuration"
          configuration
          "--no-restore"
          "--output"
          packageDirectory
          $"-p:PackageVersion={version}" ]
    |> ignore

    let packages = Directory.GetFiles(packageDirectory, "*.nupkg")
    require (packages.Length = 1) $"Expected one fresh package, found {packages.Length}."
    let packagePath = packages[0]

    require
        (Path.GetFileName packagePath = $"{packageId}.{version}.nupkg")
        "The package filename changed from the declared identity."

    inspectPackage packagePath packageId version

    run
        repositoryRoot
        "dotnet"
        [ "tool"
          "install"
          "--tool-path"
          toolDirectory
          "--configfile"
          nugetConfig
          "--add-source"
          packageDirectory
          packageId
          "--version"
          version ]
    |> ignore

    let executable =
        Path.Combine(
            toolDirectory,
            if OperatingSystem.IsWindows() then
                "dotnet-plus.exe"
            else
                "dotnet-plus"
        )

    require (File.Exists executable) "The installed package did not provide dotnet-plus."

    run fixtureDirectory "dotnet" [ "new"; "sln"; "--format"; "slnx"; "--name"; "PackageSmoke" ]
    |> ignore

    run
        fixtureDirectory
        "dotnet"
        [ "new"
          "classlib"
          "--no-restore"
          "--framework"
          "net10.0"
          "--name"
          "SmokeProject"
          "--output"
          "SmokeProject" ]
    |> ignore

    let solution = Path.Combine(fixtureDirectory, "PackageSmoke.slnx")

    let projectPath =
        Path.Combine(fixtureDirectory, "SmokeProject", "SmokeProject.csproj")

    let output, error =
        run fixtureDirectory executable [ "--json"; "solution"; solution; "add"; projectPath ]

    require (String.IsNullOrWhiteSpace error) "The installed direct command wrote stderr."

    require
        (output.Contains("\"success\":true", StringComparison.Ordinal))
        "The installed direct command did not report success."

    require
        (File.ReadAllText(solution).Contains("SmokeProject.csproj", StringComparison.Ordinal))
        "The installed direct command did not update the solution."

    printfn
        "Release package smoke passed: %s %s packed, inspected, installed, and used directly."
        packageId
        version
finally
    if Directory.Exists runRoot then
        Directory.Delete(runRoot, true)

    require (not (Directory.Exists runRoot)) "Release package smoke left its run root behind."
