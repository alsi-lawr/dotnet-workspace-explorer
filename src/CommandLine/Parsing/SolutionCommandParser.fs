namespace Dotnet.WorkspaceExplorer.CommandLine


#nowarn "3261"
#nowarn "3511"


module internal SolutionCommandParser =
    let scan =
        CommandOptionScanner.scan
            (Set.ofList [ "--solution-folder"; "-s" ])
            (Set.ofList [ "--in-root" ])
            (Set.ofList [ "--include-references" ])
