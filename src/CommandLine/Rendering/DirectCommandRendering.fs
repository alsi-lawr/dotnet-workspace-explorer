namespace Dotnet.WorkspaceExplorer.CommandLine

open Dotnet.WorkspaceExplorer.Workspaces

open System.IO
open System.Text.Json

module internal DirectCommandRendering =
    let render (result: DirectCommandResult) jsonMode (output: TextWriter) (error: TextWriter) =
        let diagnostic (value: WorkspaceDiagnostic) =
            {| severity = value.Severity.ToString() |> DotnetProcess.sanitize
               code = value.Code.Value |> DotnetProcess.sanitize
               safeMessage = value.Message |> DotnetProcess.sanitize
               artifactPath =
                value.ArtifactPath |> Option.map _.Value |> Option.map DotnetProcess.sanitize
               location =
                value.Location
                |> Option.map (fun location ->
                    {| line = location.Line
                       column = location.Column |})
               retryable = value.Retryable
               correlationId = value.CorrelationId.Value.ToString() |> DotnetProcess.sanitize |}

        if jsonMode then
            let envelope =
                {| schemaVersion = 1
                   commandId = DotnetProcess.sanitize result.CommandId
                   success = result.Success
                   revision = result.Revision |> Option.map _.Value
                   result =
                    {| summary = result.Payload.Summary |> Option.map DotnetProcess.sanitize
                       childArguments =
                        result.Payload.ChildArguments |> List.map DotnetProcess.sanitize
                       standardOutput = DotnetProcess.sanitize result.Payload.StandardOutput
                       standardError = DotnetProcess.sanitize result.Payload.StandardError |}
                   diagnostics = result.Diagnostics |> List.map diagnostic
                   externalExitCode = result.ExternalExitCode |}

            output.WriteLine(
                JsonSerializer.Serialize(
                    envelope,
                    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
                )
            )
        elif not result.Success then
            result.Diagnostics
            |> List.iter (fun value ->
                let code = DotnetProcess.sanitize value.Code.Value
                let message = DotnetProcess.sanitize value.Message
                error.WriteLine $"{code}: {message}")

        if result.Success then
            0
        else
            result.ExternalExitCode |> Option.filter ((<>) 0) |> Option.defaultValue 1
