namespace Dotnet.CLI.Plus

open System
open System.IO
open System.Xml.Linq
open Dotnet.CLI.Plus.MSBuild

module internal ProjectFolderXml =
    open ProjectXml

    let private hasMacro (value: string) =
        value.Contains("$(", StringComparison.Ordinal)

    let private normalizeRelativePath (value: string) = value.Trim().Replace('\\', '/')

    let private sourcePrefixAt (source: string) (value: string) start =
        let source = normalizeRelativePath source
        let endIndex = start + source.Length

        not (String.IsNullOrWhiteSpace source)
        && value.Length >= endIndex
        && value.AsSpan(start, source.Length).Equals(source, StringComparison.OrdinalIgnoreCase)
        && (value.Length = endIndex || value[endIndex] = '/')

    let private matchesRelative source (value: string) =
        sourcePrefixAt source (normalizeRelativePath value) 0

    let private macroReferencesSource source (value: string) =
        let value = normalizeRelativePath value

        hasMacro value
        && (sourcePrefixAt source value 0
            || [ 0 .. value.Length - 1 ]
               |> List.exists (fun index ->
                   if value[index] <> ')' then
                       false
                   else
                       let start =
                           if index + 1 < value.Length && value[index + 1] = '/' then
                               index + 2
                           else
                               index + 1

                       sourcePrefixAt source value start))

    let private projectRelativeValue (projectPath: string) (value: string) =
        let directory =
            Path.GetDirectoryName projectPath
            |> Option.ofObj
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        Path.GetFullPath(value, directory)

    let private isUnder source path =
        let relative = Path.GetRelativePath(source, path)

        relative = "."
        || not (Path.IsPathRooted relative)
           && relative <> ".."
           && not (relative.StartsWith $"..{Path.DirectorySeparatorChar}")

    let private declarationValues (document: XDocument) =
        seq {
            for element in document.Descendants() do
                for attributeName in [ "Include"; "Update"; "Remove"; "Link" ] do
                    match element.Attribute(name attributeName) |> Option.ofObj with
                    | Some attribute -> yield attribute.Value
                    | None -> ()

                if element.Name = name "Link" then
                    yield element.Value
        }

    let private declarationTokens (value: string) =
        let tokens =
            value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map normalizeRelativePath

        tokens

    let private affectedList (source: string) (value: string) =
        let tokens = declarationTokens value

        let affected = tokens |> Array.filter (matchesRelative source)
        let macroAffected = tokens |> Array.exists (macroReferencesSource source)
        macroAffected || affected.Length > 0 && tokens.Length <> 1

    let private importedDeclarationAffects sourceRelative sourcePath projectPath importPath =
        let document, _, _, _ = readDocument importPath

        declarationValues document
        |> Seq.exists (fun value ->
            affectedList sourceRelative value
            || declarationTokens value
               |> Array.exists (fun token ->
                   not (hasMacro token)
                   && isUnder sourcePath (projectRelativeValue projectPath token)))

    let ensureDirectOwnership
        (projectPath: string)
        (sourceRelative: string)
        (sourcePath: string)
        (snapshot: EvaluationSnapshot)
        (document: XDocument)
        =
        let directValues = declarationValues document |> Seq.toArray

        if directValues |> Array.exists (affectedList sourceRelative) then
            Error "An affected project declaration uses a macro or multi-value declaration."
        else
            snapshot.Imports
            |> Seq.filter (fun imported ->
                not (
                    String.Equals(imported.Value, projectPath, StringComparison.OrdinalIgnoreCase)
                )
                && File.Exists imported.Value)
            |> Seq.tryPick (fun imported ->
                try
                    if
                        importedDeclarationAffects
                            sourceRelative
                            sourcePath
                            projectPath
                            imported.Value
                    then
                        Some "An affected project declaration is owned by an import."
                    else
                        None
                with
                | :? IOException as error -> Some error.Message
                | :? UnauthorizedAccessException as error -> Some error.Message)
            |> Option.map Error
            |> Option.defaultValue (Ok())

    let private rewritePrefix source destination (value: string) =
        let normalized = normalizeRelativePath value

        if hasMacro normalized then
            Error "Folder declaration rewrites cannot contain MSBuild macros."
        elif normalized.Equals(source, StringComparison.OrdinalIgnoreCase) then
            Ok destination
        elif normalized.StartsWith($"{source}/", StringComparison.OrdinalIgnoreCase) then
            Ok(destination + normalized[source.Length ..])
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
            |> Seq.filter (fun (_, attribute) -> matchesRelative source attribute.Value)
            |> Seq.toArray

        let links =
            document.Descendants(name "Link")
            |> Seq.filter (fun element -> matchesRelative source element.Value)
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
        appendItem document "Folder" (relative.TrimEnd [| '/' |] + "/") []

    let appendExternalLink (document: XDocument) itemType (source: string) (relative: string) =
        let source = source.TrimEnd [| '/' |]
        let relative = relative.TrimEnd [| '/' |]

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
                attribute attributeName element |> Option.exists (matchesRelative source)))
        |> Seq.toArray
        |> Array.iter removeItem
