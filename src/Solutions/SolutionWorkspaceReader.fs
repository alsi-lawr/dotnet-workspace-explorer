namespace Dotnet.WorkspaceExplorer.Solutions

#nowarn "3261"
#nowarn "3262"

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Dotnet.WorkspaceExplorer.Workspaces
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open SolutionTargetResolution
open SolutionFilterReader
open SolutionDocumentProjection

module private SolutionWorkspaceOpening =
    let openWorkspace targetPath cancellationToken =
        task {
            try
                throwIfCancellationRequested cancellationToken

                match resolveTarget targetPath cancellationToken with
                | Failure failure -> return Failure failure
                | Success resolvedTarget ->
                    throwIfCancellationRequested cancellationToken

                    let caseSemantics =
                        FileSystemCaseSensitivityDetector.DetectFromExistingPath resolvedTarget

                    let targetFormat = format resolvedTarget

                    let! filter =
                        if targetFormat = WorkspaceFormat.Slnf then
                            readFilter resolvedTarget cancellationToken
                        else
                            Task.FromResult(
                                Success
                                    { BackingSolutionPath = resolvedTarget
                                      IncludedProjectPaths = None }
                            )

                    match filter with
                    | Failure failure -> return Failure failure
                    | Success selectedFilter ->
                        throwIfCancellationRequested cancellationToken

                        let backingCaseSemantics =
                            FileSystemCaseSensitivityDetector.DetectFromExistingPath
                                selectedFilter.BackingSolutionPath

                        match
                            SolutionSerializers.GetSerializerByMoniker
                                selectedFilter.BackingSolutionPath
                            |> Option.ofObj
                        with
                        | None ->
                            return
                                invalidInput
                                    "solution"
                                    "The backing solution must be a .sln or .slnx file."
                        | Some serializer ->
                            let! model =
                                serializer.OpenAsync(
                                    selectedFilter.BackingSolutionPath,
                                    cancellationToken
                                )

                            throwIfCancellationRequested cancellationToken

                            match
                                validateFilterProjects
                                    selectedFilter
                                    backingCaseSemantics
                                    model
                                    cancellationToken
                            with
                            | Failure failure -> return Failure failure
                            | Success() ->
                                let descriptor =
                                    WorkspaceDescriptor.Create(
                                        WorkspacePath.Create resolvedTarget,
                                        caseSemantics,
                                        targetFormat,
                                        WorkspaceRevision.Create 0L,
                                        WorkspaceAccess.ReadWrite
                                    )

                                let root =
                                    projectRoot
                                        descriptor
                                        backingCaseSemantics
                                        selectedFilter
                                        model
                                        cancellationToken

                                throwIfCancellationRequested cancellationToken

                                return
                                    Success(
                                        SolutionWorkspace.Create(
                                            descriptor,
                                            WorkspaceArtifactPath.Create
                                                selectedFilter.BackingSolutionPath,
                                            root
                                        )
                                    )
            with
            | :? OperationCanceledException -> return cancelled ()
            | :? SolutionException ->
                return invalidInput "solution" "The solution file is malformed."
            | :? PathTooLongException ->
                return invalidInput "targetPath" "The solution path is invalid."
            | :? IOException ->
                return internalFailure "solution.open_failed" "Failed to read the solution."
            | :? UnauthorizedAccessException ->
                return internalFailure "solution.open_failed" "Failed to read the solution."
            | :? ArgumentException
            | :? NotSupportedException ->
                return invalidInput "targetPath" "The solution path is invalid."
        }

[<AbstractClass; Sealed>]
type SolutionWorkspaceReader private () =
    static member OpenAsync
        (targetPath: string, cancellationToken: CancellationToken)
        : Task<WorkspaceOutcome<SolutionWorkspace>> =
        SolutionWorkspaceOpening.openWorkspace targetPath cancellationToken

    static member OpenAsync(targetPath: string) : Task<WorkspaceOutcome<SolutionWorkspace>> =
        SolutionWorkspaceOpening.openWorkspace targetPath CancellationToken.None
