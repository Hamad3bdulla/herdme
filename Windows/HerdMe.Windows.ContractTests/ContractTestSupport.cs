using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

sealed record HttpTestResponse(
    string StatusLine,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body
)
{
    public string BodyText => Encoding.UTF8.GetString(Body);

    public string? Header(string name)
    {
        return Headers.GetValueOrDefault(name);
    }
}

sealed class HttpTestResponseReader(Stream stream)
{
    private const int MaximumHeaderSize = 1 * 1_024 * 1_024;
    private byte[] buffered = new byte[4 * 1_024];
    private int start;
    private int count;

    public async Task<HttpTestResponse> ReadAsync(CancellationToken cancellationToken)
    {
        var headerEnd = FindHeaderEnd();
        while (headerEnd < 0)
        {
            if (count > MaximumHeaderSize)
            {
                throw new InvalidDataException("The HTTP fixture response headers were too large.");
            }
            if (!await ReadMoreAsync(cancellationToken))
            {
                throw new EndOfStreamException("The HTTP fixture response closed before its headers.");
            }
            headerEnd = FindHeaderEnd();
        }
        if (headerEnd > MaximumHeaderSize)
        {
            throw new InvalidDataException("The HTTP fixture response headers were too large.");
        }

        var headerText = Encoding.ASCII.GetString(buffered, start, headerEnd);
        Consume(headerEnd + 4);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1 ", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The HTTP fixture response status was malformed.");
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException("The HTTP fixture response header was malformed.");
            }
            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var existing)
                ? existing + ", " + value
                : value;
        }
        if (!headers.TryGetValue("Content-Length", out var contentLengthText)
            || !int.TryParse(contentLengthText, out var contentLength)
            || contentLength < 0)
        {
            throw new InvalidDataException("The HTTP fixture response has no valid Content-Length.");
        }

        var body = await ReadExactlyAsync(contentLength, cancellationToken);
        return new HttpTestResponse(lines[0], headers, body);
    }

    public async Task<bool> ReachesEndOfStreamAsync(TimeSpan timeout)
    {
        if (count > 0) return false;
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            return await stream.ReadAsync(new byte[1], cancellation.Token) == 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    private int FindHeaderEnd()
    {
        return buffered.AsSpan(start, count).IndexOf("\r\n\r\n"u8);
    }

    private async Task<byte[]> ReadExactlyAsync(
        int length,
        CancellationToken cancellationToken
    )
    {
        var output = new byte[length];
        var offset = 0;
        while (offset < output.Length)
        {
            if (count == 0 && !await ReadMoreAsync(cancellationToken))
            {
                throw new EndOfStreamException("The HTTP fixture response body closed early.");
            }
            var available = Math.Min(output.Length - offset, count);
            buffered.AsSpan(start, available).CopyTo(output.AsSpan(offset));
            Consume(available);
            offset += available;
        }
        return output;
    }

    private async Task<bool> ReadMoreAsync(CancellationToken cancellationToken)
    {
        PrepareWriteSpace();
        var read = await stream.ReadAsync(
            buffered.AsMemory(start + count, buffered.Length - start - count),
            cancellationToken
        );
        count += read;
        return read > 0;
    }

    private void PrepareWriteSpace()
    {
        if (start > 0 && start + count == buffered.Length)
        {
            buffered.AsSpan(start, count).CopyTo(buffered);
            start = 0;
        }
        if (start + count < buffered.Length) return;
        Array.Resize(ref buffered, checked(buffered.Length * 2));
    }

    private void Consume(int length)
    {
        start += length;
        count -= length;
        if (count == 0) start = 0;
    }
}

sealed class SequenceHttpMessageHandler(
    params Func<HttpRequestMessage, HttpResponseMessage>[] responses
) : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> remaining = new(responses);

    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        if (!remaining.TryDequeue(out var response))
        {
            throw new InvalidOperationException("No HTTP fixture response remains.");
        }
        return Task.FromResult(response(request));
    }
}

sealed class MemoryCredentialBackend : IWindowsCredentialBackend
{
    public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);
    public bool FailWrites { get; set; }

    public bool TryRead(string target, out string secret)
    {
        return Secrets.TryGetValue(target, out secret!);
    }

    public void Write(string target, string username, string secret)
    {
        if (FailWrites) throw new IOException("Fixture credential write failed.");
        Secrets[target] = secret;
    }

    public void Delete(string target)
    {
        Secrets.Remove(target);
    }
}

