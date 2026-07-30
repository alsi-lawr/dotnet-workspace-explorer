namespace Dotnet.WorkspaceExplorer.Testing.ScriptedDotnet

#nowarn "3261"

open System
open System.IO

module internal TemplateCreation =
    let private outputFiles (arguments: string array) =
        match InvocationSettings.setting "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_TEMPLATE_OUTPUTS" with
        | Some value ->
            value.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries
            )
        | None ->
            match InvocationSettings.argumentValue "--type" arguments with
            | Some "item" -> [| Path.Combine("Nested", "IContract.cs") |]
            | Some "project" -> [| "Generated.csproj" |]
            | _ -> [| "Template.fsproj" |]

    let create (arguments: string array) =
        let dryRun =
            arguments
            |> Array.exists (fun value ->
                value = "--dry-run"
                || value = "--dry-run=true"
                || value = "--check-only"
                || value = "--check-only=true")

        let output =
            InvocationSettings.argumentValue "--output" arguments
            |> Option.defaultValue (Directory.GetCurrentDirectory())

        let outputs =
            outputFiles arguments
            |> Array.map (fun relative -> Path.GetFullPath(relative, output))

        if dryRun then
            for path in outputs do
                Console.Out.WriteLine $"Create: {path}"
        else
            for path in outputs do
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

                let contents =
                    if Path.GetExtension(path).EndsWith("proj", StringComparison.Ordinal) then
                        "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"
                    else
                        "internal interface IContract { }"

                File.WriteAllText(path, contents)

            if
                InvocationSettings.isEnabled
                    "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_TEMPLATE_POSTACTION"
            then
                File.WriteAllText(Path.Combine(output, "postaction.txt"), "postaction")

            if
                not (OperatingSystem.IsWindows())
                && InvocationSettings.isEnabled
                    "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_TEMPLATE_BLOCK_CLEANUP"
            then
                File.SetUnixFileMode(output, UnixFileMode.None)
