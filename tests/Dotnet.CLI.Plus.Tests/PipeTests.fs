namespace Dotnet.CLI.Plus.Tests

#nowarn "3261"

open System
open System.IO
open System.Threading
open Dotnet.CLI.Plus
open Dotnet.CLI.Plus.Transport
open Microsoft.VisualStudio.SolutionPersistence.Model
open Microsoft.VisualStudio.SolutionPersistence.Serializer
open Xunit

module private PipeTest =
    let request id name parameters =
        RpcCodec.encodeFrame (Request(id, name, parameters))

    let map values = RpcValue.map values

    let decodeAll (bytes: byte array) =
        let rec consume offset frames =
            if offset = bytes.Length then
                List.rev frames
            else
                match RpcCodec.tryDecodeValue RpcCodec.secureLimits bytes[offset..] with
                | Ok(_, used) ->
                    match RpcCodec.decodeFrame RpcCodec.secureLimits bytes[offset .. offset + used - 1] with
                    | Ok frame -> consume (offset + used) (frame :: frames)
                    | Error error -> failwithf "%A" error
                | Error error -> failwithf "%A" error

        consume 0 []

    let save path model =
        let serializer = SolutionSerializers.GetSerializerByMoniker path
        serializer.SaveAsync(path, model, CancellationToken.None).GetAwaiter().GetResult()

    let initialize =
        map
            [ "protocolVersion", map [ "major", RpcValue.Integer 1L; "minor", RpcValue.Integer 4L ]
              "clientInfo", map [ "name", RpcValue.String "test" ]
              "capabilities", RpcValue.array [] ]

type PipeTests() =
    [<Fact>]
    member _.``pipe keeps stdout as framed values and serves root export refresh and shutdown``() =
        let directory =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-pipe-{Guid.NewGuid():N}")

        Directory.CreateDirectory directory |> ignore

        try
            let solution = Path.Combine(directory, "Demo.slnx")
            let model = SolutionModel()
            model.AddProject("Demo.fsproj", "Demo", null) |> ignore
            PipeTest.save solution model

            let input =
                [ PipeTest.request 1u "initialize" PipeTest.initialize
                  PipeTest.request 2u "workspace/root" RpcValue.emptyMap
                  PipeTest.request 3u "workspace/export" RpcValue.emptyMap
                  PipeTest.request 4u "workspace/refresh" RpcValue.emptyMap
                  PipeTest.request 5u "msbuild/evaluate" RpcValue.emptyMap
                  PipeTest.request 6u "shutdown" RpcValue.emptyMap ]
                |> List.collect Array.toList
                |> List.toArray

            use stdin = new MemoryStream(input)
            use stdout = new MemoryStream()
            use stderr = new StringWriter()

            let exitCode =
                Pipe.runAsync solution stdin stdout stderr CancellationToken.None |> _.Result

            Assert.Equal(0, exitCode)
            Assert.Equal(String.Empty, stderr.ToString())
            let frames = PipeTest.decodeAll (stdout.ToArray())

            Assert.Contains(
                frames,
                function
                | Notification("workspace/exportChunk", _) -> true
                | _ -> false
            )

            Assert.Contains(
                frames,
                function
                | Response(5u, Some error, _) when error.Code = "unknown_method" -> true
                | _ -> false
            )

            Assert.Equal(8, frames.Length)
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)

    [<Fact>]
    member _.``pipe rejects slnf writes before mutation handling``() =
        let directory =
            Path.Combine(Path.GetTempPath(), $"dotnet-cli-plus-pipe-filter-{Guid.NewGuid():N}")

        Directory.CreateDirectory directory |> ignore

        try
            let backing = Path.Combine(directory, "Demo.slnx")
            let filter = Path.Combine(directory, "Demo.slnf")
            PipeTest.save backing (SolutionModel())
            File.WriteAllText(filter, "{ \"solution\": { \"path\": \"Demo.slnx\" } }")

            let execute =
                PipeTest.map
                    [ "commandId", RpcValue.String "anything"
                      "arguments", RpcValue.emptyMap
                      "expectedRevision", RpcValue.Integer 0L ]

            let input =
                [ PipeTest.request 1u "initialize" PipeTest.initialize
                  PipeTest.request 2u "command/execute" execute
                  PipeTest.request 3u "shutdown" RpcValue.emptyMap ]
                |> List.collect Array.toList
                |> List.toArray

            use stdin = new MemoryStream(input)
            use stdout = new MemoryStream()
            use stderr = new StringWriter()
            Assert.Equal(0, Pipe.runAsync filter stdin stdout stderr CancellationToken.None |> _.Result)

            Assert.Contains(
                PipeTest.decodeAll (stdout.ToArray()),
                function
                | Response(2u, Some error, _) when error.Code = "unsupported_capability" -> true
                | _ -> false
            )
        finally
            if Directory.Exists directory then
                Directory.Delete(directory, true)
