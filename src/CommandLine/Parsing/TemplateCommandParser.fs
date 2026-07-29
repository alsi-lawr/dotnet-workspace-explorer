namespace Dotnet.WorkspaceExplorer.CommandLine


#nowarn "3261"
#nowarn "3511"


module internal TemplateCommandParser =
    let scan =
        CommandOptionScanner.scan
            (Set.ofList
                [ "--output"
                  "-o"
                  "--name"
                  "-n"
                  "--project"
                  "--verbosity"
                  "-v"
                  "--add-source"
                  "--nuget-source" ])
            (Set.ofList [ "--force"; "--no-update-check"; "--diagnostics"; "-d" ])
            (Set.ofList [ "--dry-run"; "--check-only" ])
