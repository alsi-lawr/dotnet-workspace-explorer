namespace Dotnet.WorkspaceExplorer.CommandLine

open Dotnet.WorkspaceExplorer.Workspaces

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

module internal DirectCommandRendering =
    let private ansi =
        Regex("\u001b(?:[@-_][0-?]*[ -/]*[@-~]|\\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled)

    let private sanitize value =
        ansi.Replace(value, String.Empty)
        |> Seq.filter (fun character ->
            character = '\t'
            || character = '\n'
            || character = '\r'
            || character >= ' ' && character <> '\u007f')
        |> String.Concat

    let render
        (result: Result<DirectCommandCompletion, DirectCommandFailure>)
        jsonMode
        (output: TextWriter)
        (error: TextWriter)
        =
        let diagnostic (value: WorkspaceDiagnostic) =
            {| severity = value.Severity.ToString() |> sanitize
               code = value.Code.Value |> sanitize
               safeMessage = value.Message |> sanitize
               artifactPath = value.ArtifactPath |> Option.map _.Value |> Option.map sanitize
               location =
                value.Location
                |> Option.map (fun location ->
                    {| line = location.Line
                       column = location.Column |})
               retryable = value.Retryable
               correlationId = value.CorrelationId.Value.ToString() |> sanitize |}

        let commandId, revision, commandOutput, diagnostics =
            match result with
            | Ok completion -> completion.CommandId, completion.Revision, completion.Output, []
            | Error failure -> failure.CommandId, None, None, [ failure.Diagnostic ]

        if jsonMode then
            let envelope =
                {| schemaVersion = 1
                   commandId = sanitize commandId
                   success = Result.isOk result
                   revision = revision |> Option.map _.Value
                   result = {| output = commandOutput |> Option.map sanitize |}
                   diagnostics = diagnostics |> List.map diagnostic |}

            output.WriteLine(
                JsonSerializer.Serialize(
                    envelope,
                    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
                )
            )
        else
            match result with
            | Ok completion -> completion.Output |> Option.iter output.Write
            | Error failure ->
                let code = sanitize failure.Diagnostic.Code.Value
                let message = sanitize failure.Diagnostic.Message
                error.WriteLine $"{code}: {message}"

        if Result.isOk result then 0 else 1
