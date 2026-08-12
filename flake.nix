{
  description = ".NET 10 development shell for dotnet-workspace-explorer";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs =
    { nixpkgs, ... }:
    let
      supportedSystems = [
        "x86_64-linux"
        "aarch64-linux"
        "x86_64-darwin"
        "aarch64-darwin"
      ];
      forAllSystems = nixpkgs.lib.genAttrs supportedSystems;
      formattingTools = pkgs:
        let
          fantomas_7_0_5 = pkgs.buildDotnetGlobalTool {
            pname = "fantomas";
            version = "7.0.5";
            nugetHash = "sha256-fseS0ORahl/iK/uZmGOooTmrny8YL1KEwNNq27VxLj0=";
            dotnet-runtime = pkgs.dotnet-sdk_10;
          };
          csharpier_1_3_0 = pkgs.buildDotnetGlobalTool {
            pname = "csharpier";
            version = "1.3.0";
            nugetHash = "sha256-hwieEoQTcATyKZIZ7CQSWANPBv+pEShg6cDXU5EIexU=";
            dotnet-runtime = pkgs.dotnet-sdk_10;
          };
        in
        [
          pkgs.dotnet-sdk_10
          pkgs.git
          fantomas_7_0_5
          csharpier_1_3_0
        ];
      workspaceExplorer = system:
        let
          pkgs = import nixpkgs { inherit system; };
          inherit (pkgs) lib;
        in
        pkgs.buildDotnetModule {
          pname = "dotnet-workspace-explorer";
          version = "0.5.0";

          src = lib.fileset.toSource {
            root = ./.;
            fileset = lib.fileset.unions [
              ./Directory.Build.props
              ./Directory.Packages.props
              ./global.json
              ./src
            ];
          };

          projectFile = "src/WorkspaceExplorer/Dotnet.WorkspaceExplorer.fsproj";
          nugetDeps = ./nix/deps.json;
          dotnet-sdk = pkgs.dotnet-sdk_10;
          dotnet-runtime = pkgs.dotnet-runtime_10;
          selfContainedBuild = true;
          executables = [ "dotnet-we" ];

          postInstall = ''
            mv \
              "$out/lib/$pname/Dotnet.WorkspaceExplorer" \
              "$out/lib/$pname/dotnet-we"
          '';

          meta = {
            description = "Explore and edit .NET solutions from the command line or an editor";
            homepage = "https://github.com/alsi-lawr/dotnet-workspace-explorer";
            license = pkgs.lib.licenses.mit;
            mainProgram = "dotnet-we";
          };
        };
    in
    {
      packages = forAllSystems (
        system:
        let
          package = workspaceExplorer system;
        in
        {
          default = package;
          dotnet-workspace-explorer = package;
        }
      );

      apps = forAllSystems (system: {
        default = {
          type = "app";
          program = "${workspaceExplorer system}/bin/dotnet-we";
        };
      });

      devShells = forAllSystems (
        system:
        let
          pkgs = import nixpkgs { inherit system; };
        in
        {
          default = pkgs.mkShellNoCC {
            packages = formattingTools pkgs ++ [ pkgs.fsautocomplete ];

            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
            NUGET_XMLDOC_MODE = "skip";
          };
        }
      );
    };
}
