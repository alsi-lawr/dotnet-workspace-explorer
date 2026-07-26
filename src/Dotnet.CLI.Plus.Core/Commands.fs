namespace Dotnet.CLI.Plus.Core

open System
open System.Collections.Immutable

type CommandId private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> Validation.nonEmpty (nameof value) |> CommandId

    override _.ToString() = value

    override _.Equals(other) =
        match other with
        | :? CommandId as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        StringComparer.Ordinal.GetHashCode value

type CommandParameterId private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> Validation.nonEmpty (nameof value) |> CommandParameterId

    override _.ToString() = value

    override _.Equals(other) =
        match other with
        | :? CommandParameterId as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        StringComparer.Ordinal.GetHashCode value

type CommandParameterType =
    | Text = 0
    | Path = 1
    | Boolean = 2
    | Integer = 3
    | NodeId = 4
    | Choice = 5
    | TextArray = 6

type CommandAccess =
    | Read = 0
    | Write = 1

type CommandChoiceId private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> Validation.nonEmpty (nameof value) |> CommandChoiceId

    override _.ToString() = value

    override _.Equals(other) =
        match other with
        | :? CommandChoiceId as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        StringComparer.Ordinal.GetHashCode value

type CommandParameterDescriptor =
    private
        { Id: CommandParameterId
          Type: CommandParameterType
          IsRequired: bool
          DisplayName: string }

    member this.ParameterId = this.Id
    member this.ParameterType = this.Type
    member this.Required = this.IsRequired
    member this.Name = this.DisplayName

    static member Create
        (id: CommandParameterId, parameterType: CommandParameterType, isRequired: bool, displayName: string)
        =
        if isNull (box id) then
            nullArg (nameof id)

        displayName |> Validation.nonEmpty (nameof displayName) |> ignore

        { Id = id
          Type = parameterType
          IsRequired = isRequired
          DisplayName = displayName }

type CommandDescriptor =
    private
        { Id: CommandId
          DisplayName: string
          Access: CommandAccess
          Parameters: ImmutableArray<CommandParameterDescriptor>
          TargetKinds: ImmutableArray<WorkspaceNodeKind> }

    member this.CommandId = this.Id
    member this.Name = this.DisplayName
    member this.CommandAccess = this.Access
    member this.ParameterDescriptors = this.Parameters
    member this.ApplicableTargetKinds = this.TargetKinds

    member this.RequiredCapability =
        match this.Access with
        | CommandAccess.Read -> WorkspaceCapabilityId.Read
        | _ -> WorkspaceCapabilityId.Write

    static member Create
        (
            id: CommandId,
            displayName: string,
            access: CommandAccess,
            parameters: seq<CommandParameterDescriptor>,
            targetKinds: seq<WorkspaceNodeKind>
        ) =
        if isNull (box id) then
            nullArg (nameof id)

        if isNull (box parameters) then
            nullArg (nameof parameters)

        if isNull (box targetKinds) then
            nullArg (nameof targetKinds)

        displayName |> Validation.nonEmpty (nameof displayName) |> ignore
        let parameterArray = parameters |> ImmutableArray.CreateRange

        if
            parameterArray |> Seq.map _.Id |> Seq.distinct |> Seq.length
            <> parameterArray.Length
        then
            invalidArg (nameof parameters) "Command parameter IDs must be unique."

        { Id = id
          DisplayName = displayName
          Access = access
          Parameters = parameterArray
          TargetKinds = targetKinds |> Seq.distinct |> ImmutableArray.CreateRange }

type CommandParameterValue =
    | Text of value: string
    | Path of value: WorkspaceArtifactPath
    | Boolean of value: bool
    | Integer of value: int64
    | Node of value: NodeId
    | Choice of value: CommandChoiceId
    | TextArray of values: ImmutableArray<string>

type CommandArgument =
    { ParameterId: CommandParameterId
      Value: CommandParameterValue }

type CommandArguments =
    private
    | CommandArguments of ImmutableArray<CommandArgument>

    member this.Values = let (CommandArguments values) = this in values

    static member Create(values: seq<CommandArgument>) =
        if isNull (box values) then
            nullArg (nameof values)

        let arguments = values |> ImmutableArray.CreateRange

        if
            arguments |> Seq.map _.ParameterId |> Seq.distinct |> Seq.length
            <> arguments.Length
        then
            invalidArg (nameof values) "Command argument parameter IDs must be unique."

        CommandArguments arguments

type CommandMutationRequest =
    { CommandId: CommandId
      TargetId: NodeId option
      Arguments: CommandArguments
      ExpectedRevision: WorkspaceRevision }

type OperationId private (value: Guid) =
    member _.Value = value
    static member New() = OperationId(Guid.NewGuid())
    override _.ToString() = value.ToString("N")

    override _.Equals(other) =
        match other with
        | :? OperationId as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() = value.GetHashCode()

type OperationState =
    | Queued = 0
    | Running = 1
    | Succeeded = 2
    | Failed = 3
    | Cancelled = 4

type WorkspaceOperation =
    { Id: OperationId
      State: OperationState
      Revision: WorkspaceRevision
      Diagnostics: ImmutableArray<WorkspaceDiagnostic> }
