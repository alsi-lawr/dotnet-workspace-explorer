namespace Dotnet.WorkspaceExplorer.CommandLine

open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions
open Dotnet.WorkspaceExplorer.WorkspaceEditing

#nowarn "3261"
#nowarn "3511"

open System
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq
open Dotnet.WorkspaceExplorer.Workspaces
open Dotnet.WorkspaceExplorer.Solutions

module internal ReferencePostconditions =
    let verifyReferences operation (project: string) framework operands =
        if List.isEmpty operands then
            Error(
                DirectCommandFailures.invalid
                    "Reference mutations require one or more project operands."
            )
        else
            let projectDirectory =
                Path.GetDirectoryName project
                |> Option.ofObj
                |> Option.defaultValue (Directory.GetCurrentDirectory())

            let document = XDocument.Load project

            let comparer =
                match FileSystemCaseSensitivityDetector.DetectFromExistingPath project with
                | FileSystemCaseSensitivity.Insensitive -> StringComparer.OrdinalIgnoreCase
                | _ -> StringComparer.Ordinal

            let references =
                PackagePostconditions.descendants "ProjectReference" document
                |> Seq.choose (fun reference ->
                    PackagePostconditions.attribute "Include" reference
                    |> Option.map (fun value ->
                        Path.GetFullPath(value, projectDirectory),
                        PackagePostconditions.itemGroupCondition reference))
                |> Seq.filter (fun (_, condition) ->
                    PackagePostconditions.conditionAppliesToFramework framework condition)
                |> Seq.map fst
                |> Seq.toList

            let requested =
                operands |> List.map (fun value -> Path.GetFullPath(value, projectDirectory))

            let correct =
                match operation with
                | ReferenceAdd ->
                    requested
                    |> List.forall (fun value ->
                        references
                        |> List.exists (fun reference -> comparer.Equals(reference, value)))
                | ReferenceRemove ->
                    requested
                    |> List.forall (fun value ->
                        references
                        |> List.exists (fun reference -> comparer.Equals(reference, value))
                        |> not)
                | _ -> true

            if correct then
                Ok None
            else
                Error(
                    DirectCommandFailures.verification
                        "The refreshed project does not contain the requested reference state."
                )
