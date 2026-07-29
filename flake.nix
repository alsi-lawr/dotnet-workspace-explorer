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
    in
    {
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

      checks = forAllSystems (
        system:
        let
          pkgs = import nixpkgs { inherit system; };
        in
        {
          formatting-tools = pkgs.runCommand "dotnet-workspace-explorer-formatting-tools" {
            nativeBuildInputs = formattingTools pkgs;
          } ''
            export DOTNET_CLI_HOME="$TMPDIR"
            dotnet --version | grep -E '^10[.]'
            git --version
            fantomas --version | grep -F 'Fantomas v7.0.5'
            csharpier --version | grep -F '1.3.0'
            touch "$out"
          '';
        }
      );
    };
}
