namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.ProjectEvaluation
open Dotnet.WorkspaceExplorer.WorkspaceIndex

#nowarn "3261"

open System
open System.Globalization
open System.IO
open System.Text
open System.Xml
open System.Xml.Linq

module internal MsBuildProjectDocument =
    let readDocument path =
        let bytes = File.ReadAllBytes path
        use stream = new MemoryStream(bytes)
        let settings = XmlReaderSettings()
        settings.IgnoreWhitespace <- false
        use reader = XmlReader.Create(stream, settings)
        let document = XDocument.Load(reader, LoadOptions.PreserveWhitespace)

        let declarationEncoding =
            document.Declaration
            |> Option.ofObj
            |> Option.bind (fun declaration ->
                try
                    declaration.Encoding |> Option.ofObj |> Option.map Encoding.GetEncoding
                with :? ArgumentException ->
                    None)

        let bomCandidates: (byte array * Encoding) list =
            [ UTF32Encoding(false, true).GetPreamble(), UTF32Encoding(false, true) :> Encoding
              UTF32Encoding(true, true).GetPreamble(), UTF32Encoding(true, true) :> Encoding
              Encoding.Unicode.GetPreamble(), Encoding.Unicode
              Encoding.BigEndianUnicode.GetPreamble(), Encoding.BigEndianUnicode
              Encoding.UTF8.GetPreamble(), Encoding.UTF8 ]

        let bomEncoding =
            bomCandidates
            |> Seq.tryPick (fun (preamble, candidate) ->
                if
                    preamble.Length > 0
                    && bytes.Length >= preamble.Length
                    && bytes[.. preamble.Length - 1] = preamble
                then
                    Some candidate
                else
                    None)

        let encoding =
            bomEncoding
            |> Option.orElse declarationEncoding
            |> Option.defaultValue Encoding.UTF8

        let hasPreamble = bomEncoding.IsSome

        let text = encoding.GetString bytes

        let lineEnding =
            if text.Contains("\r\n", StringComparison.Ordinal) then
                "\r\n"
            elif text.Contains '\n' then
                "\n"
            else
                Environment.NewLine

        document, encoding, hasPreamble, lineEnding

    type private EncodingWriter(encoding: Encoding) =
        inherit StringWriter(CultureInfo.InvariantCulture)
        override _.Encoding = encoding

    let saveDocument (document: XDocument) (encoding: Encoding) hasPreamble lineEnding =
        use writer = new EncodingWriter(encoding)
        let settings = XmlWriterSettings()
        settings.Encoding <- encoding
        settings.Indent <- false
        settings.NewLineHandling <- NewLineHandling.None
        settings.OmitXmlDeclaration <- isNull document.Declaration

        use xml = XmlWriter.Create(writer, settings)
        document.Save xml
        xml.Flush()

        let text =
            if lineEnding = "\r\n" then
                writer.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n")
            else
                writer.ToString()

        let contents = encoding.GetBytes text

        if hasPreamble then
            Array.append (encoding.GetPreamble()) contents
        else
            contents

    let name local = XName.Get local

    let attribute local (element: XElement) =
        element.Attribute(name local) |> Option.ofObj |> Option.map _.Value

    let itemGroup (document: XDocument) =
        document.Root.Elements(name "ItemGroup") |> Seq.tryHead

    let newline (document: XDocument) =
        document.DescendantNodes()
        |> Seq.choose (function
            | :? XText as value when value.Value.Contains("\r\n", StringComparison.Ordinal) ->
                Some "\r\n"
            | :? XText as value when value.Value.Contains '\n' -> Some "\n"
            | _ -> None)
        |> Seq.tryHead
        |> Option.defaultValue Environment.NewLine

    let appendItemWith
        (document: XDocument)
        (itemType: string)
        attributeName
        (includeValue: string)
        (metadata: (string * string) list)
        =
        let group =
            itemGroup document
            |> Option.defaultWith (fun () ->
                let group = XElement(name "ItemGroup")
                document.Root.Add(XText $"{newline document}  ", group, XText(newline document))
                group)

        let item = XElement(name itemType, XAttribute(name attributeName, includeValue))

        for metadataName, metadataValue in metadata do
            item.Add(XElement(name metadataName, metadataValue))

        group.Add(XText $"{newline document}    ", item, XText $"{newline document}  ")

    let appendItem document itemType includeValue metadata =
        appendItemWith document itemType "Include" includeValue metadata

    let appendUpdate document itemType includeValue metadata =
        appendItemWith document itemType "Update" includeValue metadata

    let appendRemove (document: XDocument) (itemType: string) (includeValue: string) =
        let exists =
            document.Descendants(name itemType)
            |> Seq.exists (fun item -> attribute "Remove" item = Some includeValue)

        if not exists then
            appendItemWith document itemType "Remove" includeValue []

    let removeItem (item: XElement) = item.Remove()

    let containsItemGlob (document: XDocument) itemType includeValue =
        document.Descendants(name itemType)
        |> Seq.exists (fun item -> attribute "Include" item = Some includeValue)

    let replaceProject path document encoding preamble lineEnding =
        WorkspaceEditAction.ReplaceFile(path, saveDocument document encoding preamble lineEnding)
