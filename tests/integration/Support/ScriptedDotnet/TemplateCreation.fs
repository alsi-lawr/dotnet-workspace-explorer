namespace Dotnet.WorkspaceExplorer.Testing.ScriptedDotnet

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Xml.Linq

module internal TemplateCreation =
    let create (arguments: string array) =
        let dryRun =
            arguments
            |> Array.exists (fun value ->
                value = "--dry-run"
                || value = "--dry-run=true"
                || value = "--check-only"
                || value = "--check-only=true")

        if not dryRun then
            let output =
                InvocationSettings.argumentValue "--output" arguments
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            Directory.CreateDirectory output |> ignore

            File.WriteAllText(
                Path.Combine(output, "Template.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"
            )
