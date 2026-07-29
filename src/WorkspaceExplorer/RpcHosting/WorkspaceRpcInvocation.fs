namespace Dotnet.WorkspaceExplorer

open System
open System.Globalization

[<RequireQualifiedAccess>]
type internal WorkspaceRpcInvocation =
    | NotPipeRelated
    | InvalidPipeStartup
    | ValidPipeStartup of target: string * exportCapacity: int

[<RequireQualifiedAccess>]
module internal WorkspaceRpcInvocation =
    let private reservedStartupToken (argument: string) =
        argument = "--pipe"
        || argument = "--export-workers"
        || argument.StartsWith("--pipe=", StringComparison.Ordinal)
        || argument.StartsWith("--export-workers=", StringComparison.Ordinal)

    let parse (arguments: string array) =
        match arguments with
        | [| "workspace"; target; "--pipe" |] -> WorkspaceRpcInvocation.ValidPipeStartup(target, 3)
        | [| "workspace"; target; "--pipe"; "--export-workers"; value |] ->
            match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, capacity when capacity > 0 ->
                WorkspaceRpcInvocation.ValidPipeStartup(target, capacity)
            | _ -> WorkspaceRpcInvocation.InvalidPipeStartup
        | _ when arguments |> Array.exists reservedStartupToken ->
            WorkspaceRpcInvocation.InvalidPipeStartup
        | _ -> WorkspaceRpcInvocation.NotPipeRelated
