namespace Dotnet.CLI.Plus

open System
open System.IO
open System.Security.Cryptography
open System.Text

[<RequireQualifiedAccess>]
module internal ContinuationTokens =
    type Payload =
        { WorkspaceId: string
          ParentId: string
          Offset: int
          Revision: int64 }

    let private writeString (writer: BinaryWriter) (value: string) =
        let bytes = Encoding.UTF8.GetBytes value
        writer.Write bytes.Length
        writer.Write bytes

    let private readString (reader: BinaryReader) =
        let length = reader.ReadInt32()

        if length < 0 || length > 4096 then
            invalidArg "token" "The continuation token contains an invalid string."

        reader.ReadBytes length
        |> fun bytes ->
            if bytes.Length <> length then
                invalidArg "token" "The continuation token is truncated."

            UTF8Encoding(false, true).GetString bytes

    let create (secret: byte array) (payload: Payload) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8, true)
        writeString writer payload.WorkspaceId
        writeString writer payload.ParentId
        writer.Write payload.Offset
        writer.Write payload.Revision
        writer.Flush()
        let body = stream.ToArray()
        use hmac = new HMACSHA256(secret)
        let signature = hmac.ComputeHash body
        $"{Convert.ToBase64String body}.{Convert.ToBase64String signature}"

    let tryParse (secret: byte array) (value: string) =
        try
            let parts = value.Split('.', StringSplitOptions.None)

            if parts.Length <> 2 then
                None
            else
                let body = Convert.FromBase64String parts[0]
                let supplied = Convert.FromBase64String parts[1]
                use hmac = new HMACSHA256(secret)
                let expected = hmac.ComputeHash body

                if not (CryptographicOperations.FixedTimeEquals(expected, supplied)) then
                    None
                else
                    use stream = new MemoryStream(body, false)
                    use reader = new BinaryReader(stream, Encoding.UTF8, true)

                    let payload =
                        { WorkspaceId = readString reader
                          ParentId = readString reader
                          Offset = reader.ReadInt32()
                          Revision = reader.ReadInt64() }

                    if payload.Offset < 0 || stream.Position <> stream.Length then
                        None
                    else
                        Some payload
        with
        | :? ArgumentException
        | :? EndOfStreamException
        | :? DecoderFallbackException
        | :? FormatException -> None
