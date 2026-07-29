namespace Dotnet.WorkspaceExplorer.CommandLine


#nowarn "3261"
#nowarn "3511"

open System
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq

module internal PackagePostconditions =
    let descendants name (document: XDocument) =
        document.Descendants()
        |> Seq.filter (fun element -> element.Name.LocalName = name)

    let attribute name (element: XElement) =
        element.Attribute(XName.Get name) |> Option.ofObj |> Option.map _.Value

    let itemGroupCondition (element: XElement) =
        element.Parent
        |> Option.ofObj
        |> Option.bind (attribute "Condition")
        |> Option.defaultValue String.Empty

    let conditionAppliesToFramework framework condition =
        match framework with
        | None -> true
        | Some _ when String.IsNullOrWhiteSpace condition -> true
        | Some expected ->
            let compact = Regex.Replace(condition, "\\s+", String.Empty)

            [ $"'$(TargetFramework)'=='{expected}'"
              $"\"$(TargetFramework)\"==\"{expected}\""
              $"$(TargetFramework)=='{expected}'"
              $"$(TargetFramework)==\"{expected}\"" ]
            |> List.exists (fun candidate ->
                String.Equals(compact, candidate, StringComparison.OrdinalIgnoreCase))

    let packageSubject (value: string) =
        let index = value.LastIndexOf '@'

        if index > 0 then
            value.Substring(0, index), Some(value.Substring(index + 1))
        else
            value, None

    let private centralVersion (project: string) (id: string) condition =
        let rec find directory =
            let candidate = Path.Combine(directory, "Directory.Packages.props")

            if File.Exists candidate then
                let document = XDocument.Load candidate

                descendants "PackageVersion" document
                |> Seq.tryFind (fun element ->
                    attribute "Include" element
                    |> Option.orElseWith (fun () -> attribute "Update" element)
                    |> Option.exists (fun value ->
                        String.Equals(value, id, StringComparison.OrdinalIgnoreCase))
                    && String.Equals(
                        itemGroupCondition element,
                        condition,
                        StringComparison.Ordinal
                    ))
                |> Option.bind (fun element ->
                    attribute "Version" element
                    |> Option.orElseWith (fun () ->
                        element.Elements()
                        |> Seq.tryFind (fun child -> child.Name.LocalName = "Version")
                        |> Option.map _.Value))
            else
                match Directory.GetParent directory with
                | null -> None
                | parent -> find parent.FullName

        Path.GetDirectoryName project |> Option.ofObj |> Option.bind find

    let verifyPackage operation (project: string) framework operands =
        match operands with
        | [] -> Error(DirectCommandFailures.invalid "Package mutations require a package ID.")
        | subjects ->
            let document = XDocument.Load project
            let references = descendants "PackageReference" document |> Seq.toList

            let present subject =
                let id, version = packageSubject subject

                references
                |> List.exists (fun reference ->
                    let condition = itemGroupCondition reference

                    let matchesId =
                        attribute "Include" reference
                        |> Option.orElseWith (fun () -> attribute "Update" reference)
                        |> Option.exists (fun actual ->
                            String.Equals(actual, id, StringComparison.OrdinalIgnoreCase))

                    let actualVersion =
                        attribute "Version" reference
                        |> Option.orElseWith (fun () ->
                            reference.Elements()
                            |> Seq.tryFind (fun child -> child.Name.LocalName = "Version")
                            |> Option.map _.Value)

                    let effectiveVersion =
                        actualVersion
                        |> Option.orElseWith (fun () -> centralVersion project id condition)

                    matchesId
                    && conditionAppliesToFramework framework condition
                    && version |> Option.forall (fun expected -> effectiveVersion = Some expected))

            let correct =
                match operation with
                | PackageAdd
                | PackageUpdate -> subjects |> List.forall present
                | PackageRemove -> subjects |> List.forall (present >> not)
                | _ -> true

            if correct then
                Ok None
            else
                Error(
                    DirectCommandFailures.verification
                        "The refreshed project does not contain the requested package state."
                )
