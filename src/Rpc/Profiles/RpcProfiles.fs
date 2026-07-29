namespace Dotnet.WorkspaceExplorer.Rpc

open Dotnet.WorkspaceExplorer.Workspaces

open System
open System.Collections.Immutable

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
            ImmutableDictionary.CreateBuilder<string, RpcMethodDescriptor> StringComparer.Ordinal

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
