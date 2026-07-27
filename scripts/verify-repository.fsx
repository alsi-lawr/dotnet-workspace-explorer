open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Xml.Linq

let fail message =
    invalidOp $"repository audit: {message}"

let require condition message =
    if not condition then
        fail message

let repositoryRoot = Directory.GetCurrentDirectory()

let gitFiles (arguments: string list) =
    let start = ProcessStartInfo "git"
    start.WorkingDirectory <- repositoryRoot
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.UseShellExecute <- false

    for argument in arguments do
        start.ArgumentList.Add argument

    use child = Process.Start start
    let output = child.StandardOutput.ReadToEnd()
    let error = child.StandardError.ReadToEnd()
    child.WaitForExit()
    let command = String.Join(" ", arguments)
    require (child.ExitCode = 0) $"git {command} failed: {error.Trim()}"

    output.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList

let trackedFSharp = gitFiles [ "ls-files"; "*.fs"; "*.fsi"; "*.fsx" ]

require (not trackedFSharp.IsEmpty) "No tracked F# source files were found."

let testProjects =
    gitFiles [ "ls-files"; "tests/**/*.fsproj" ]
    |> List.filter (fun path ->
        File.ReadAllText(Path.Combine(repositoryRoot, path))
        |> fun contents -> contents.Contains "<IsTestProject>true</IsTestProject>")

require
    (testProjects.Length = 3)
    $"Expected exactly three F# IsTestProject projects, found {testProjects.Length}."

let packageVersions =
    XDocument.Load(Path.Combine(repositoryRoot, "Directory.Packages.props"))
    |> fun document -> document.Descendants(XName.Get "PackageVersion")
    |> Seq.choose (fun element ->
        let includeAttribute = element.Attribute(XName.Get "Include")
        let versionAttribute = element.Attribute(XName.Get "Version")

        if isNull includeAttribute || isNull versionAttribute then
            None
        else
            Some(includeAttribute.Value, versionAttribute.Value))
    |> Map.ofSeq

for name, version in [ "FsUnit.Xunit", "7.1.1"; "xunit.v3", "3.2.2" ] do
    require
        (packageVersions |> Map.tryFind name = Some version)
        $"{name} must be pinned to {version}."

for project in testProjects do
    let document = XDocument.Load(Path.Combine(repositoryRoot, project))

    let packageReferences =
        document.Descendants(XName.Get "PackageReference")
        |> Seq.choose (fun element ->
            let includeAttribute = element.Attribute(XName.Get "Include")

            if isNull includeAttribute then
                None
            else
                Some includeAttribute.Value)
        |> Set.ofSeq

    let useMtp =
        document.Descendants(XName.Get "UseMicrosoftTestingPlatformRunner")
        |> Seq.exists (fun element ->
            String.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))

    require
        (packageReferences.Contains "FsUnit.Xunit")
        $"{project} does not reference FsUnit.Xunit."

    require (packageReferences.Contains "xunit.v3") $"{project} does not reference xunit.v3."
    require useMtp $"{project} does not select the Microsoft Testing Platform runner."

let globalJson =
    JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "global.json")))

let mutable runner = Unchecked.defaultof<JsonElement>

require
    (globalJson.RootElement.TryGetProperty("test", &runner)
     && runner.TryGetProperty("runner", &runner)
     && runner.GetString() = "Microsoft.Testing.Platform")
    "global.json does not select Microsoft.Testing.Platform."

let bindingPattern =
    Regex("^\\s*(?:let|member\\s+_\\.)``should\\s+[^`]+``", RegexOptions.Compiled)

let attributePattern =
    Regex("^\\s*\\[<(Fact|Theory)(?:\\([^]]*\\))?>\\]", RegexOptions.Compiled)

let testSources =
    testProjects
    |> List.collect (fun project ->
        let projectDirectory = Path.GetDirectoryName project

        trackedFSharp
        |> List.filter (fun path ->
            path.StartsWith(projectDirectory + "/", StringComparison.Ordinal)))

for source in testSources do
    let lines = File.ReadAllLines(Path.Combine(repositoryRoot, source))

    for index in 0 .. lines.Length - 1 do
        if attributePattern.IsMatch lines[index] then
            let bindingIndex =
                [ index + 1 .. lines.Length - 1 ]
                |> List.tryFind (fun candidate ->
                    not (String.IsNullOrWhiteSpace lines[candidate])
                    && not (
                        lines[candidate].TrimStart().StartsWith("[<", StringComparison.Ordinal)
                    ))

            match bindingIndex with
            | Some candidate when bindingPattern.IsMatch lines[candidate] -> ()
            | _ ->
                fail
                    $"{source}:{index + 1} Fact/Theory must bind a double-backticked `should ` scenario."

type MatchScope =
    { Indent: int
      mutable BranchIndent: int option
      mutable SawBranch: bool
      mutable BlankAfterBranch: bool }

let matchKeywordPattern = Regex("\\bmatch\\b", RegexOptions.Compiled)
let branchPattern = Regex("^(\\s*)\\|", RegexOptions.Compiled)

let indentation (line: string) = line.Length - line.TrimStart().Length

let auditMatchBranches (source: string) (lines: string array) =
    let scopes = ResizeArray<MatchScope>()

    for index in 0 .. lines.Length - 1 do
        let line = lines[index]

        if String.IsNullOrWhiteSpace line then
            for scope in scopes do
                if scope.SawBranch then
                    scope.BlankAfterBranch <- true
        else
            let indent = indentation line
            let branch = branchPattern.Match line

            if not branch.Success then
                let surviving =
                    scopes |> Seq.filter (fun scope -> indent > scope.Indent) |> Seq.toList

                scopes.Clear()
                scopes.AddRange surviving

                for scope in scopes do
                    if scope.SawBranch then
                        scope.BlankAfterBranch <- false
            else
                let scope =
                    scopes
                    |> Seq.toList
                    |> List.rev
                    |> List.tryFind (fun candidate ->
                        candidate.Indent <= indent
                        && (candidate.BranchIndent = Some indent || candidate.BranchIndent.IsNone))

                match scope with
                | Some candidate ->
                    if candidate.SawBranch && candidate.BlankAfterBranch then
                        fail $"{source}:{index + 1} has a blank line between match branches."

                    candidate.SawBranch <- true
                    candidate.BranchIndent <- Some indent
                    candidate.BlankAfterBranch <- false
                | None -> ()

            if matchKeywordPattern.IsMatch line then
                scopes.Add
                    { Indent = indent
                      BranchIndent = None
                      SawBranch = false
                      BlankAfterBranch = false }

for source in trackedFSharp do
    File.ReadAllLines(Path.Combine(repositoryRoot, source))
    |> auditMatchBranches source

printfn
    "repository audit: %d tracked F# files; three native-MTP test projects; package, runner, naming, and match-branch contracts pass"
    trackedFSharp.Length
