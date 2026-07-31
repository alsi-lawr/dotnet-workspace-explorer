namespace Dotnet.WorkspaceExplorer.Workspaces

open System
open System.Collections.Immutable

type CommandId private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> WorkspaceValue.nonEmpty (nameof value) |> CommandId

    override _.ToString() = value

    override _.Equals other =
        match other with
        | :? CommandId as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        StringComparer.Ordinal.GetHashCode value

type CommandParameterId private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> WorkspaceValue.nonEmpty (nameof value) |> CommandParameterId

    override _.ToString() = value

    override _.Equals other =
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
    | NodeIdArray = 7

type CommandAccess =
    | Read = 0
    | Write = 1

type CommandChoiceId private (value: string) =
    member _.Value = value

    static member Create(value: string) =
        value |> WorkspaceValue.nonEmpty (nameof value) |> CommandChoiceId

    override _.ToString() = value

    override _.Equals other =
        match other with
        | :? CommandChoiceId as candidate -> value = candidate.Value
        | _ -> false

    override _.GetHashCode() =
        StringComparer.Ordinal.GetHashCode value

type CommandParameterDescriptor =
    private
        { ParameterIdValue: CommandParameterId
          ParameterTypeValue: CommandParameterType
          RequiredValue: bool
          ParameterNameValue: string }

    member this.Id = this.ParameterIdValue
    member this.Type = this.ParameterTypeValue
    member this.Required = this.RequiredValue
    member this.Name = this.ParameterNameValue

    static member Create
        (
            id: CommandParameterId,
            parameterType: CommandParameterType,
            isRequired: bool,
            displayName: string
        ) =
        if isNull (box id) then
            nullArg (nameof id)

        displayName |> WorkspaceValue.nonEmpty (nameof displayName) |> ignore

        { ParameterIdValue = id
          ParameterTypeValue = parameterType
          RequiredValue = isRequired
          ParameterNameValue = displayName }

type CommandDescriptor =
    private
        { CommandIdValue: CommandId
          CommandNameValue: string
          CommandAccessValue: CommandAccess
          ParametersValue: ImmutableArray<CommandParameterDescriptor>
          TargetKindsValue: ImmutableArray<WorkspaceNodeKind> }

    member this.Id = this.CommandIdValue
    member this.Name = this.CommandNameValue
    member this.Access = this.CommandAccessValue
    member this.Parameters = this.ParametersValue
    member this.TargetKinds = this.TargetKindsValue

    member this.IsRequiredCapability =
        match this.CommandAccessValue with
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

        displayName |> WorkspaceValue.nonEmpty (nameof displayName) |> ignore
        let parameterArray = parameters |> ImmutableArray.CreateRange

        if
            parameterArray |> Seq.map _.Id |> Seq.distinct |> Seq.length
            <> parameterArray.Length
        then
            invalidArg (nameof parameters) "Command parameter IDs must be unique."

        { CommandIdValue = id
          CommandNameValue = displayName
          CommandAccessValue = access
          ParametersValue = parameterArray
          TargetKindsValue = targetKinds |> Seq.distinct |> ImmutableArray.CreateRange }

type CommandParameterValue =
    | Text of value: string
    | Path of value: WorkspaceArtifactPath
    | Boolean of value: bool
    | Integer of value: int64
    | Node of value: WorkspaceNodeId
    | Choice of value: CommandChoiceId
    | TextArray of values: ImmutableArray<string>
    | NodeIdArray of values: ImmutableArray<WorkspaceNodeId>

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
      TargetWorkspaceNodeId: WorkspaceNodeId option
      Arguments: CommandArguments
      ExpectedRevision: WorkspaceRevision }
