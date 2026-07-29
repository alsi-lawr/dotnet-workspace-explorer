namespace Dotnet.WorkspaceExplorer.CommandLine


#nowarn "3261"
#nowarn "3511"

open System
open System.IO
open System.Text.Json

type internal TemplateEngineInstallation =
    { Packages: string list
      Mounts: string list }

module internal TemplateEngineInstallationReader =
    let Root () =
        Environment.GetEnvironmentVariable "DOTNET_CLI_HOME"
        |> Option.ofObj
        |> Option.defaultValue (Environment.GetFolderPath Environment.SpecialFolder.UserProfile)
        |> fun home -> Path.Combine(home, ".templateengine")

    let Read (root: string) =
        try
            let caches =
                if Directory.Exists root then
                    Directory.EnumerateFiles(
                        root,
                        "templatecache.json",
                        SearchOption.AllDirectories
                    )
                    |> Seq.toList
                else
                    []

            let values =
                caches
                |> List.collect (fun cache ->
                    use document = JsonDocument.Parse(File.ReadAllText cache)
                    let mutable mounts = Unchecked.defaultof<JsonElement>

                    if document.RootElement.TryGetProperty("MountPointsInfo", &mounts) then
                        mounts.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                    else
                        [])

            Ok { Packages = values; Mounts = values }
        with
        | :? JsonException ->
            Error(DirectCommandFailures.invalid "The template cache is malformed.")
        | :? IOException ->
            Error(DirectCommandFailures.internalFailure "The template cache could not be read.")

    let Contains (subject: string, state: TemplateEngineInstallation) =
        let id = subject.Split("::", 2)[0]

        state.Packages
        |> List.exists (fun value ->
            let name = Path.GetFileNameWithoutExtension value in

            String.Equals(name, id, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase)
            || String.Equals(value, subject, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(id + "::", StringComparison.OrdinalIgnoreCase))
