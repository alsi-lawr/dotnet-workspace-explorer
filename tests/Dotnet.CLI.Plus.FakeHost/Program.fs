namespace Dotnet.CLI.Plus.FakeHost

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Xml.Linq

type FakeHostAssemblyMarker = class end

module Program =
    let private setting name =
        Environment.GetEnvironmentVariable name |> Option.ofObj

    let private argumentValue name (arguments: string array) =
        arguments
        |> Array.pairwise
        |> Array.tryPick (function
            | option, value when option = name -> Some value
            | _ -> None)

    let private isEnabled name =
        match setting name with
        | Some value ->
            value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        | None -> false

    let private recordInvocation arguments =
        match setting "DOTNET_PLUS_FAKE_HOST_CAPTURE" with
        | Some path ->
            let directory = Path.GetDirectoryName path

            if not (String.IsNullOrEmpty directory) then
                Directory.CreateDirectory directory |> ignore

            let line = JsonSerializer.Serialize arguments + Environment.NewLine
            File.AppendAllText(path, line)
        | None -> ()

    let private signalAndWait () =
        match setting "DOTNET_PLUS_FAKE_HOST_MARKER" with
        | Some path ->
            let temporary =
                Path.Combine(
                    Path.GetDirectoryName path,
                    $".{Path.GetFileName path}.{Guid.NewGuid():N}"
                )

            File.WriteAllText(temporary, string Environment.ProcessId)
            File.Move(temporary, path)
        | None -> ()

        match setting "DOTNET_PLUS_FAKE_HOST_RELEASE" with
        | Some path when not (File.Exists path) ->
            use watcher =
                new FileSystemWatcher(Path.GetDirectoryName path, Path.GetFileName path)

            watcher.EnableRaisingEvents <- true

            if not (File.Exists path) then
                watcher.WaitForChanged(WatcherChangeTypes.Created ||| WatcherChangeTypes.Renamed)
                |> ignore
        | _ -> ()

    let private descendants localName (document: XDocument) =
        document.Descendants()
        |> Seq.filter (fun element -> element.Name.LocalName = localName)

    let private attribute name (element: XElement) =
        element.Attributes()
        |> Seq.tryFind (fun candidate -> candidate.Name.LocalName = name)

    let private projectItemGroup (document: XDocument) =
        match document.Root with
        | null -> invalidOp "The fake host cannot mutate a project without a root element."
        | root ->
            let itemGroup = XElement(root.Name.Namespace + "ItemGroup")
            root.Add itemGroup
            itemGroup

    let private addElement name attributes (document: XDocument) =
        match document.Root with
        | null -> invalidOp "The fake host cannot mutate a project without a root element."
        | root ->
            let element = XElement(root.Name.Namespace + name)

            for attributeName, value in attributes do
                element.SetAttributeValue(XName.Get attributeName, value)

            let itemGroup = projectItemGroup document
            itemGroup.Add element
            element

    let private saveProject (path: string) (document: XDocument) = document.Save path

    let private positionalArguments (arguments: string array) =
        let optionsWithValues = set [ "--framework"; "--output"; "--project"; "--version" ]

        let rec collect index values =
            if index >= arguments.Length then
                List.rev values
            elif optionsWithValues.Contains arguments[index] then
                collect (index + 2) values
            elif arguments[index].StartsWith("--", StringComparison.Ordinal) then
                collect (index + 1) values
            else
                collect (index + 1) (arguments[index] :: values)

        collect 2 []

    let private referencePath (projectPath: string) (reference: string) =
        if Path.IsPathRooted reference then
            Path.GetRelativePath(Path.GetDirectoryName projectPath, reference)
        else
            reference

    let private mutateReference verb (arguments: string array) =
        match argumentValue "--project" arguments, positionalArguments arguments with
        | Some projectPath, reference :: _ ->
            let document = XDocument.Load projectPath
            let includePath = referencePath projectPath reference

            if verb = "add" then
                let item = XElement(document.Root.Name.Namespace + "ProjectReference")
                item.SetAttributeValue(XName.Get "Include", includePath)
                let itemGroup = projectItemGroup document
                itemGroup.Add item
            else
                descendants "ProjectReference" document
                |> Seq.filter (fun item ->
                    match attribute "Include" item with
                    | Some includeAttribute ->
                        includeAttribute.Value.Equals(
                            includePath,
                            StringComparison.OrdinalIgnoreCase
                        )
                    | None -> false)
                |> Seq.toArray
                |> Array.iter _.Remove()

            saveProject projectPath document
        | _ -> invalidOp "The fake reference command requires --project and a reference path."

    let private packageIdentity (arguments: string array) =
        positionalArguments arguments |> List.tryHead

    let private packageVersion (arguments: string array) = argumentValue "--version" arguments

    let private mutatePackage verb (arguments: string array) =
        match argumentValue "--project" arguments, packageIdentity arguments with
        | Some projectPath, Some identity ->
            let document = XDocument.Load projectPath

            let package, inlineVersion =
                match identity.LastIndexOf '@' with
                | separator when separator > 0 ->
                    identity[.. separator - 1], Some identity[separator + 1 ..]
                | _ -> identity, None

            let matches =
                descendants "PackageReference" document
                |> Seq.filter (fun item ->
                    match attribute "Include" item with
                    | Some includeAttribute ->
                        includeAttribute.Value.Equals(package, StringComparison.OrdinalIgnoreCase)
                    | None -> false)
                |> Seq.toArray

            if verb = "remove" then
                matches |> Array.iter _.Remove()
            else
                let item =
                    match matches |> Array.tryHead with
                    | Some existing -> existing
                    | None -> addElement "PackageReference" [ "Include", package ] document

                match packageVersion arguments |> Option.orElse inlineVersion with
                | Some version -> item.SetAttributeValue(XName.Get "Version", version)
                | None -> ()

            saveProject projectPath document
        | _ when verb = "update" -> ()
        | _ -> invalidOp "The fake package command requires --project and a package ID."

    let private createTemplate (arguments: string array) =
        let dryRun =
            arguments
            |> Array.exists (fun value ->
                value = "--dry-run"
                || value = "--dry-run=true"
                || value = "--check-only"
                || value = "--check-only=true")

        if not dryRun then
            let output =
                argumentValue "--output" arguments
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            Directory.CreateDirectory output |> ignore

            File.WriteAllText(
                Path.Combine(output, "Template.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"
            )

    let private runCanonical (arguments: string array) =
        recordInvocation arguments
        signalAndWait ()

        match setting "DOTNET_PLUS_FAKE_HOST_OUTPUT_LENGTH" with
        | Some value ->
            match Int32.TryParse value with
            | true, length when length > 0 -> Console.Out.Write(String('x', length))
            | _ -> ()
        | None -> ()

        let dryRun =
            arguments
            |> Array.exists (fun value ->
                value = "--dry-run"
                || value = "--dry-run=true"
                || value = "--check-only"
                || value = "--check-only=true")

        let mutated =
            match arguments |> Array.toList with
            | "reference" :: verb :: _ when verb = "add" || verb = "remove" ->
                mutateReference verb arguments
                true
            | "package" :: verb :: _ when verb = "add" || verb = "remove" || verb = "update" ->
                mutatePackage verb arguments
                true
            | "new" :: _ when argumentValue "--output" arguments |> Option.isSome && not dryRun ->
                createTemplate arguments
                true
            | _ -> false

        if mutated && isEnabled "DOTNET_PLUS_FAKE_HOST_FAIL_AFTER_MUTATION" then
            Console.Error.Write "fake host failure after mutation"
            23
        else
            0

    [<EntryPoint>]
    let main arguments =
        match arguments |> Array.toList, setting "DOTNET_PLUS_FAKE_HOST_MODE" with
        | [ "--child" ], _ ->
            use blocked = new ManualResetEventSlim false
            blocked.Wait()
            0
        | _, Some "capture" ->
            Console.Out.Write(JsonSerializer.Serialize arguments)
            0
        | _, Some "stream" ->
            Console.Out.Write "\u001b[31mfirst\u001b[0m"
            Console.Out.Flush()
            signalAndWait ()

            Console.Out.Write "second"
            0
        | _, Some "failure" ->
            Console.Error.Write "\u001b[31mfailure\u001b[0m"
            23
        | _, Some "marker" ->
            match setting "DOTNET_PLUS_FAKE_HOST_MARKER" with
            | Some path -> File.WriteAllText(path, "started")
            | None -> ()

            0
        | _, Some "create-output" ->
            let output =
                arguments
                |> Array.pairwise
                |> Array.tryPick (function
                    | "--output", value
                    | "-o", value -> Some value
                    | _ -> None)
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            Directory.CreateDirectory output |> ignore
            File.WriteAllText(Path.Combine(output, "created-by-fake.txt"), "created")
            0
        | _, Some "canonical" -> runCanonical arguments
        | _, Some "tree" ->
            let startInfo = ProcessStartInfo()

            startInfo.FileName <-
                Environment.ProcessPath
                |> Option.ofObj
                |> Option.defaultWith (fun () ->
                    invalidOp "The fake host process path is unavailable.")

            startInfo.UseShellExecute <- false
            startInfo.ArgumentList.Add "--child"
            use child = Process.Start startInfo

            if isNull child then
                invalidOp "The fake child host could not be started."

            match setting "DOTNET_PLUS_FAKE_HOST_CHILD_PID" with
            | Some path ->
                let temporary =
                    Path.Combine(
                        Path.GetDirectoryName path,
                        $".{Path.GetFileName path}.{Guid.NewGuid():N}"
                    )

                File.WriteAllText(temporary, string child.Id)
                File.Move(temporary, path)
            | None -> ()

            child.WaitForExit()
            0
        | _ -> 0
