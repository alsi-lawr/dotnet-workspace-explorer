namespace Dotnet.WorkspaceExplorer.Testing.ScriptedDotnet

#nowarn "3261"

open System
open System.IO
open System.Xml.Linq

module internal ProjectFileEditing =
    let private descendants localName (document: XDocument) =
        document.Descendants()
        |> Seq.filter (fun element -> element.Name.LocalName = localName)

    let private attribute name (element: XElement) =
        element.Attributes()
        |> Seq.tryFind (fun candidate -> candidate.Name.LocalName = name)

    let private projectItemGroup (document: XDocument) =
        match document.Root with
        | null -> invalidOp "The scripted dotnet cannot mutate a project without a root element."
        | root ->
            let itemGroup = XElement(root.Name.Namespace + "ItemGroup")
            root.Add itemGroup
            itemGroup

    let private addElement name attributes (document: XDocument) =
        match document.Root with
        | null -> invalidOp "The scripted dotnet cannot mutate a project without a root element."
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

    let mutateReference verb (arguments: string array) =
        match
            InvocationSettings.argumentValue "--project" arguments, positionalArguments arguments
        with
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

    let private packageVersion (arguments: string array) =
        InvocationSettings.argumentValue "--version" arguments

    let mutatePackage verb (arguments: string array) =
        match InvocationSettings.argumentValue "--project" arguments, packageIdentity arguments with
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
