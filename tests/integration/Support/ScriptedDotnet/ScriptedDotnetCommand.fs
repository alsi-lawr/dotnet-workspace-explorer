namespace Dotnet.WorkspaceExplorer.Testing.ScriptedDotnet

#nowarn "3261"

open System

module internal ScriptedDotnetCommand =
    let run (arguments: string array) =
        InvocationSettings.recordInvocation arguments

        match
            InvocationSettings.setting "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_OUTPUT_LENGTH"
        with
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

        if not dryRun then
            InvocationSettings.signalAndWait ()

        let mutated =
            match arguments |> Array.toList with
            | "reference" :: verb :: _ when verb = "add" || verb = "remove" ->
                ProjectFileEditing.mutateReference verb arguments
                true
            | "package" :: verb :: _ when verb = "add" || verb = "remove" || verb = "update" ->
                ProjectFileEditing.mutatePackage verb arguments
                true
            | "new" :: _ when InvocationSettings.argumentValue "--output" arguments |> Option.isSome ->
                TemplateCreation.create arguments
                not dryRun
            | _ -> false

        if
            mutated
            && InvocationSettings.isEnabled
                "DOTNET_WORKSPACE_EXPLORER_SCRIPTED_DOTNET_FAIL_AFTER_EDIT"
        then
            Console.Error.Write "scripted dotnet failure after mutation"
            23
        else
            0
