using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HerdMe.Windows.Services;

public sealed record FastCgiResult(byte[] StandardOutput, byte[] StandardError);

public sealed class FastCgiClient
{
    private const byte Version = 1;
    private const byte BeginRequest = 1;
    private const byte EndRequest = 3;
    private const byte Parameters = 4;
    private const byte StandardInput = 5;
    private const byte StandardOutput = 6;
    private const byte StandardError = 7;
    private const ushort RequestIdentifier = 1;
    private const int MaximumContentLength = 65_535;

    public async Task<FastCgiResult> PerformAsync(
        int port,
        IReadOnlyDictionary<string, string> parameters,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default
    )
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        await using var stream = client.GetStream();
        await WriteRecordAsync(
            stream,
            BeginRequest,
            new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 },
            cancellationToken
        );
        await WriteRecordsAsync(stream, Parameters, Encode(parameters), cancellationToken);
        await WriteRecordAsync(stream, Parameters, ReadOnlyMemory<byte>.Empty, cancellationToken);
        await WriteRecordsAsync(stream, StandardInput, body, cancellationToken);
        await WriteRecordAsync(stream, StandardInput, ReadOnlyMemory<byte>.Empty, cancellationToken);

        using var output = new MemoryStream();
        using var errors = new MemoryStream();
        var header = new byte[8];
        while (true)
        {
            await ReadExactlyAsync(stream, header, cancellationToken);
            if (header[0] != Version) throw new InvalidDataException("FastCGI returned an invalid version.");
            var requestId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
            var padding = header[6];
            var content = new byte[length];
            await ReadExactlyAsync(stream, content, cancellationToken);
            if (padding > 0) await ReadExactlyAsync(stream, new byte[padding], cancellationToken);
            if (requestId != RequestIdentifier) continue;
            switch (header[1])
            {
                case StandardOutput:
                    await output.WriteAsync(content, cancellationToken);
                    break;
                case StandardError:
                    await errors.WriteAsync(content, cancellationToken);
                    break;
                case EndRequest:
                    return new FastCgiResult(output.ToArray(), errors.ToArray());
            }
            if (output.Length + errors.Length > 64 * 1_024 * 1_024)
            {
                throw new InvalidDataException("FastCGI response exceeded the HerdMe limit.");
            }
        }
    }

    private static byte[] Encode(IReadOnlyDictionary<string, string> parameters)
    {
        using var output = new MemoryStream();
        foreach (var parameter in parameters.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var name = Encoding.UTF8.GetBytes(parameter.Key);
            var value = Encoding.UTF8.GetBytes(parameter.Value);
            WriteLength(output, name.Length);
            WriteLength(output, value.Length);
            output.Write(name);
            output.Write(value);
        }
        return output.ToArray();
    }

    private static void WriteLength(Stream stream, int length)
    {
        if (length < 128)
        {
            stream.WriteByte((byte)length);
            return;
        }
        Span<byte> encoded = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, (uint)length | 0x80000000);
        stream.Write(encoded);
    }

    private static async Task WriteRecordsAsync(
        Stream stream,
        byte type,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken
    )
    {
        var offset = 0;
        while (offset < content.Length)
        {
            var length = Math.Min(MaximumContentLength, content.Length - offset);
            await WriteRecordAsync(stream, type, content.Slice(offset, length), cancellationToken);
            offset += length;
        }
    }

    private static async Task WriteRecordAsync(
        Stream stream,
        byte type,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken
    )
    {
        var padding = (8 - content.Length % 8) % 8;
        var header = new byte[8];
        header[0] = Version;
        header[1] = type;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), RequestIdentifier);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), checked((ushort)content.Length));
        header[6] = (byte)padding;
        await stream.WriteAsync(header, cancellationToken);
        if (!content.IsEmpty) await stream.WriteAsync(content, cancellationToken);
        if (padding > 0) await stream.WriteAsync(new byte[padding], cancellationToken);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (count == 0) throw new EndOfStreamException("FastCGI closed the connection early.");
            offset += count;
        }
    }
}
