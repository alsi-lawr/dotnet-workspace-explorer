namespace Dotnet.WorkspaceExplorer

type internal GitDecorationState =
    | Changed
    | Added

type internal GitStatusSnapshot =
    { Available: bool
      Decorations: (string * GitDecorationState) array }
