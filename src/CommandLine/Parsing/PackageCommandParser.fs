namespace Dotnet.WorkspaceExplorer.CommandLine


#nowarn "3261"
#nowarn "3511"


module internal PackageCommandParser =
    let scan =
        CommandOptionScanner.scan
            (Set.ofList
                [ "--project"
                  "--file"
                  "--version"
                  "-v"
                  "--framework"
                  "-f"
                  "--source"
                  "-s"
                  "--configfile"
                  "--package-directory"
                  "--verbosity" ])
            (Set.ofList [ "--prerelease"; "--vulnerable"; "--no-restore"; "-n"; "--interactive" ])
            Set.empty
