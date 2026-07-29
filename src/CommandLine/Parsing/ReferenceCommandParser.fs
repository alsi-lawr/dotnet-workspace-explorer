namespace Dotnet.WorkspaceExplorer.CommandLine


#nowarn "3261"
#nowarn "3511"


module internal ReferenceCommandParser =
    let scan =
        CommandOptionScanner.scan
            (Set.ofList [ "--project"; "--framework"; "-f" ])
            (Set.ofList [ "--interactive"; "--no-restore" ])
            Set.empty
