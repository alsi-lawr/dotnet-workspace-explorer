namespace Dotnet.CLI.Plus

open System
open System.Xml.Linq

module internal ProjectFolderXml =
    open ProjectXml

    let private hasMacro (value: string) =
        value.Contains("$(", StringComparison.Ordinal)

    let private rewritePrefix source destination (value: string) =
        if hasMacro value then
            Error "Folder declaration rewrites cannot contain MSBuild macros."
        elif value.Equals(source, StringComparison.OrdinalIgnoreCase) then
            Ok destination
        elif value.StartsWith($"{source}/", StringComparison.OrdinalIgnoreCase) then
            Ok(destination + value[source.Length ..])
        else
            Ok value

    let rewriteOwnedDescendants source destination (document: XDocument) =
        let affected =
            document.Descendants()
            |> Seq.collect (fun element ->
                [ "Include"; "Update"; "Remove"; "Link" ]
                |> Seq.choose (fun attributeName ->
                    element.Attribute(name attributeName)
                    |> Option.ofObj
                    |> Option.map (fun attribute -> attributeName, attribute)))
            |> Seq.filter (fun (_, attribute) ->
                attribute.Value.Equals(source, StringComparison.OrdinalIgnoreCase)
                || attribute.Value.StartsWith($"{source}/", StringComparison.OrdinalIgnoreCase))
            |> Seq.toArray

        let links =
            document.Descendants(name "Link")
            |> Seq.filter (fun element ->
                element.Value.Equals(source, StringComparison.OrdinalIgnoreCase)
                || element.Value.StartsWith($"{source}/", StringComparison.OrdinalIgnoreCase))
            |> Seq.toArray

        affected
        |> Array.fold
            (fun state (_, attribute) ->
                state
                |> Result.bind (fun () ->
                    rewritePrefix source destination attribute.Value
                    |> Result.map (fun replacement -> attribute.Value <- replacement)))
            (Ok())
        |> Result.bind (fun () ->
            links
            |> Array.fold
                (fun state link ->
                    state
                    |> Result.bind (fun () ->
                        rewritePrefix source destination link.Value
                        |> Result.map (fun replacement -> link.Value <- replacement)))
                (Ok()))

    let appendFolder (document: XDocument) (relative: string) =
        appendItem document "Folder" (relative.TrimEnd([| '/' |]) + "/") []

    let appendExternalLink (document: XDocument) itemType (source: string) (relative: string) =
        let source = source.TrimEnd([| '/' |])
        let relative = relative.TrimEnd([| '/' |])

        appendItem
            document
            itemType
            (source + "/**/*")
            [ "Link", relative + "/%(RecursiveDir)%(Filename)%(Extension)" ]

    let removeOwnedDescendants source (document: XDocument) =
        document.Descendants()
        |> Seq.filter (fun element ->
            [ "Include"; "Update" ]
            |> Seq.exists (fun attributeName ->
                attribute attributeName element
                |> Option.exists (fun value ->
                    value.Equals(source, StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith($"{source}/", StringComparison.OrdinalIgnoreCase))))
        |> Seq.toArray
        |> Array.iter removeItem

    let rejectAmbiguousDescendantRewrite (document: XDocument) =
        document.Descendants()
        |> Seq.collect (fun element ->
            [ "Include"; "Update"; "Remove"; "Link" ]
            |> Seq.choose (fun attributeName -> attribute attributeName element))
        |> Seq.tryFind hasMacro
        |> Option.map (fun _ -> Error "Folder declaration rewrites cannot contain MSBuild macros.")
        |> Option.defaultValue (Ok())
