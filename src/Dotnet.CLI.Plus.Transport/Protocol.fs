namespace Dotnet.CLI.Plus.Transport

open System
open System.Collections.Immutable

[<RequireQualifiedAccess>]
type RpcValue =
    | Nil
    | Boolean of bool
    | Integer of int64
    | Unsigned of uint64
    | Float of float
    | String of string
    | Binary of byte array
    | Array of ImmutableArray<RpcValue>
    | Map of ImmutableDictionary<string, RpcValue>

[<RequireQualifiedAccess>]
module RpcValue =
    let map (values: seq<string * RpcValue>) =
        let builder =
            ImmutableDictionary.CreateBuilder<string, RpcValue>(StringComparer.Ordinal)

        for key, value in values do
            if String.IsNullOrWhiteSpace key then
                invalidArg (nameof values) "MessagePack map keys must be non-empty strings."

            if builder.ContainsKey key then
                invalidArg (nameof values) $"Duplicate MessagePack map key '{key}'."

            builder.Add(key, value)

        RpcValue.Map(builder.ToImmutable())

    let array (values: seq<RpcValue>) =
        values |> ImmutableArray.CreateRange |> RpcValue.Array

    let emptyMap = map Seq.empty

    let tryField name value =
        match value with
        | RpcValue.Map fields ->
            fields.TryGetValue name
            |> function
                | true, found -> Some found
                | _ -> None
        | _ -> None

    let requireMap name value =
        match value with
        | RpcValue.Map fields -> fields
        | _ -> invalidArg name "Expected a string-key map."

    let optionalField name (fields: ImmutableDictionary<string, RpcValue>) =
        match fields.TryGetValue name with
        | true, value -> Some value
        | _ -> None

    let requireField name (fields: ImmutableDictionary<string, RpcValue>) =
        match fields.TryGetValue name with
        | true, value -> value
        | _ -> invalidArg name $"Missing required field '{name}'."

    let requireArray name value =
        match value with
        | RpcValue.Array values -> values
        | _ -> invalidArg name "Expected an array."

    let ensureOnly name allowed (fields: ImmutableDictionary<string, RpcValue>) =
        let allowedNames =
            ImmutableHashSet.CreateRange<string>(StringComparer.Ordinal, allowed)

        match fields.Keys |> Seq.tryFind (allowedNames.Contains >> not) with
        | Some field -> invalidArg name $"Unknown field '{field}'."
        | None -> ()

    let requireString name value =
        match value with
        | RpcValue.String text -> text
        | _ -> invalidArg name "Expected a string."

    let requireUnsigned32 name value =
        match value with
        | RpcValue.Unsigned number when number <= uint64 UInt32.MaxValue -> uint32 number
        | RpcValue.Integer number when number >= 0L && number <= int64 UInt32.MaxValue -> uint32 number
        | _ -> invalidArg name "Expected a non-negative uint32-compatible integer."

    let requireInteger name value =
        match value with
        | RpcValue.Integer number -> number
        | RpcValue.Unsigned number when number <= uint64 Int64.MaxValue -> int64 number
        | _ -> invalidArg name "Expected an integer."

type RpcError =
    { Code: string
      Message: string
      Data: RpcValue option }

type RpcFrame =
    | Request of messageId: uint32 * methodName: string * parameters: RpcValue
    | Response of messageId: uint32 * error: RpcError option * result: RpcValue
    | Notification of methodName: string * parameters: RpcValue

type RpcMethodClassification =
    | Control
    | Read
    | Mutation
    | NotificationMethod

type RpcMethodDescriptor =
    { Name: string
      Classification: RpcMethodClassification }

type RpcProfile =
    { Name: string
      VersionMajor: int
      VersionMinor: int
      Methods: ImmutableDictionary<string, RpcMethodDescriptor> }

[<RequireQualifiedAccess>]
module RpcProfile =
    let create name major minor (methods: seq<RpcMethodDescriptor>) =
        if String.IsNullOrWhiteSpace name || major < 0 || minor < 0 then
            invalidArg (nameof name) "An RPC profile requires a name and non-negative version."

        let descriptors =
            ImmutableDictionary.CreateBuilder<string, RpcMethodDescriptor>(StringComparer.Ordinal)

        for descriptor in methods do
            if
                String.IsNullOrWhiteSpace descriptor.Name
                || descriptors.ContainsKey descriptor.Name
            then
                invalidArg (nameof methods) "RPC profile methods must have unique non-empty names."

            descriptors.Add(descriptor.Name, descriptor)

        { Name = name
          VersionMajor = major
          VersionMinor = minor
          Methods = descriptors.ToImmutable() }

    let publicProfile =
        create
            "dotnet-cli-plus/workspace"
            1
            0
            [ for name in
                  [ "initialize"
                    "workspace/root"
                    "workspace/children"
                    "workspace/export"
                    "workspace/refresh"
                    "command/list"
                    "command/describe"
                    "command/preview"
                    "command/execute"
                    "operation/cancel"
                    "shutdown" ] do
                  { Name = name
                    Classification =
                      if name = "initialize" || name = "shutdown" then Control
                      elif name = "command/execute" then Mutation
                      else Read }
              for name in
                  [ "workspace/delta"
                    "workspace/reset"
                    "workspace/exportChunk"
                    "operation/progress"
                    "operation/output"
                    "operation/completed"
                    "test/update"
                    "test/attachment" ] do
                  { Name = name
                    Classification = NotificationMethod } ]
