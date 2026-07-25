using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace HerdMe.Windows.Services;

public sealed record LocalSiteDefinition(string Domain, string Path, int? PhpFastCgiPort = null);

public sealed class LocalHttpSiteServer : IAsyncDisposable
{
    private const int MaximumHeaderSize = 1 * 1_024 * 1_024;
    private const int MaximumBodySize = 32 * 1_024 * 1_024;
    private readonly FastCgiClient fastCgiClient = new();
    private readonly ConcurrentDictionary<int, Task> sessions = new();
    private CancellationTokenSource? cancellation;
    private TcpListener? listener;
    private Task? acceptTask;
    private IReadOnlyDictionary<string, SiteRoute> routes = new Dictionary<string, SiteRoute>();
    private int fastCgiPort;
    private int sessionIdentifier;
    private X509Certificate2? certificate;

    public int? Port { get; private set; }

    public bool IsRunning => listener is not null;

    public Task<int> StartAsync(
        IEnumerable<LocalSiteDefinition> sites,
        int phpFastCgiPort,
        int preferredPort = 80,
        int fallbackPort = 8_080,
        X509Certificate2? serverCertificate = null,
        CancellationToken cancellationToken = default
    )
    {
        if (IsRunning && Port is not null) return Task.FromResult(Port.Value);
        var normalized = sites.ToDictionary(
            site => NormalizeHost(site.Domain),
            site => new SiteRoute(DocumentRoot(site.Path), site.PhpFastCgiPort ?? phpFastCgiPort),
            StringComparer.OrdinalIgnoreCase
        );
        if (normalized.Count == 0) throw new InvalidOperationException("No local sites were provided.");

        var selectedPort = AvailablePort(preferredPort, fallbackPort);
        routes = normalized;
        fastCgiPort = phpFastCgiPort;
        certificate = serverCertificate;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listener = new TcpListener(IPAddress.Loopback, selectedPort);
        listener.Start(128);
        Port = selectedPort;
        acceptTask = AcceptLoopAsync(listener, cancellation.Token);
        return Task.FromResult(selectedPort);
    }

    public async Task StopAsync()
    {
        var source = Interlocked.Exchange(ref cancellation, null);
        source?.Cancel();
        listener?.Stop();
        listener = null;
        Port = null;
        if (acceptTask is not null)
        {
            try { await acceptTask; }
            catch (OperationCanceledException) { }
            catch (SocketException) when (source?.IsCancellationRequested == true) { }
        }
        acceptTask = null;
        if (!sessions.IsEmpty)
        {
            try { await Task.WhenAll(sessions.Values).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException) { }
        }
        sessions.Clear();
        certificate?.Dispose();
        certificate = null;
        source?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(TcpListener activeListener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await activeListener.AcceptTcpClientAsync(cancellationToken);
            var identifier = Interlocked.Increment(ref sessionIdentifier);
            var task = HandleClientAsync(client, cancellationToken);
            sessions[identifier] = task;
            _ = task.ContinueWith(
                completedTask =>
                {
                    _ = completedTask.Exception;
                    sessions.TryRemove(identifier, out _);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            using var networkStream = client.GetStream();
            SslStream? secureStream = null;
            Stream stream = networkStream;
            HttpRequestData? request = null;
            try
            {
                if (certificate is not null)
                {
                    secureStream = new SslStream(networkStream, leaveInnerStreamOpen: true);
                    await secureStream.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificate = certificate,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            ClientCertificateRequired = false
                        },
                        cancellationToken
                    );
                    stream = secureStream;
                }
                request = await ReadRequestAsync(stream, cancellationToken);
                var host = request.Header("Host")?.Split(':', 2)[0];
                if (host is null || !routes.TryGetValue(NormalizeHost(host), out var route))
                {
                    await WriteErrorAsync(stream, "404 Not Found", cancellationToken);
                    return;
                }
                var response = await ResponseAsync(request, route, cancellationToken);
                await stream.WriteAsync(response, cancellationToken);
            }
            catch (HttpRequestException error)
            {
                await WriteErrorAsync(stream, error.Status, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception error) when (error is IOException or SocketException or InvalidDataException)
            {
                if (request is not null)
                {
                    await WriteErrorAsync(stream, "502 Bad Gateway", cancellationToken);
                }
            }
            finally
            {
                secureStream?.Dispose();
            }
        }
    }

    private async Task<byte[]> ResponseAsync(
        HttpRequestData request,
        SiteRoute route,
        CancellationToken cancellationToken
    )
    {
        var target = RequestTarget.Parse(request.Target);
        var resource = Resolve(route.DocumentRoot, target.Path);
        if (resource.StaticFile is not null)
        {
            if (request.Method is not ("GET" or "HEAD"))
            {
                return ErrorResponse("405 Method Not Allowed");
            }
            var file = await File.ReadAllBytesAsync(resource.StaticFile, cancellationToken);
            return MakeResponse(
                "200 OK",
                [
                    ("Content-Type", MimeType(Path.GetExtension(resource.StaticFile))),
                    ("Content-Length", file.Length.ToString()),
                    ("Cache-Control", "no-cache"),
                    ("Connection", "close")
                ],
                request.Method == "HEAD" ? [] : file
            );
        }

        var parameters = FastCgiParameters(
            request,
            target,
            route.DocumentRoot,
            resource,
            certificate is not null
        );
        var result = await fastCgiClient.PerformAsync(
            route.PhpFastCgiPort,
            parameters,
            request.Body,
            cancellationToken
        );
        if (result.StandardError.Length > 0) WritePhpLog(result.StandardError);
        return ParseFastCgiResponse(result.StandardOutput);
    }

    private static Dictionary<string, string> FastCgiParameters(
        HttpRequestData request,
        RequestTarget target,
        string documentRoot,
        ResolvedResource resource,
        bool secure
    )
    {
        var host = request.Header("Host")?.Split(':', 2)[0] ?? "localhost";
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GATEWAY_INTERFACE"] = "CGI/1.1",
            ["SERVER_SOFTWARE"] = "HerdMe",
            ["SERVER_PROTOCOL"] = request.Protocol,
            ["REQUEST_METHOD"] = request.Method,
            ["REQUEST_URI"] = request.Target,
            ["QUERY_STRING"] = target.Query,
            ["DOCUMENT_ROOT"] = documentRoot,
            ["DOCUMENT_URI"] = target.Path,
            ["SCRIPT_FILENAME"] = resource.ScriptFile!,
            ["SCRIPT_NAME"] = resource.ScriptName!,
            ["SERVER_NAME"] = host,
            ["SERVER_PORT"] = secure ? "443" : "80",
            ["REQUEST_SCHEME"] = secure ? "https" : "http",
            ["REMOTE_ADDR"] = "127.0.0.1",
            ["REMOTE_PORT"] = "0",
            ["SERVER_ADDR"] = "127.0.0.1",
            ["CONTENT_LENGTH"] = request.Body.Length.ToString()
        };
        if (secure) values["HTTPS"] = "on";
        values["HTTP_X_FORWARDED_PROTO"] = secure ? "https" : "http";
        values["HTTP_X_FORWARDED_FOR"] = "127.0.0.1";
        if (!string.IsNullOrEmpty(resource.PathInfo)) values["PATH_INFO"] = resource.PathInfo;
        if (request.Header("Content-Type") is { } contentType) values["CONTENT_TYPE"] = contentType;
        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var key = "HTTP_" + new string(header.Key.ToUpperInvariant().Select(character =>
                char.IsAsciiLetterOrDigit(character) ? character : '_'
            ).ToArray());
            values[key] = values.TryGetValue(key, out var existing)
                ? existing + ", " + header.Value
                : header.Value;
        }
        return values;
    }

    private static ResolvedResource Resolve(string documentRoot, string requestPath)
    {
        var root = Path.GetFullPath(documentRoot).TrimEnd(Path.DirectorySeparatorChar);
        var relative = requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("403 Forbidden");
        }

        if (Directory.Exists(candidate))
        {
            foreach (var index in new[] { "index.html", "index.htm" })
            {
                var file = Path.Combine(candidate, index);
                if (File.Exists(file)) return new ResolvedResource(file, null, null, null);
            }
            var phpIndex = Path.Combine(candidate, "index.php");
            if (File.Exists(phpIndex))
            {
                var name = requestPath.EndsWith('/') ? requestPath + "index.php" : requestPath + "/index.php";
                return new ResolvedResource(null, phpIndex, name, null);
            }
        }
        else if (File.Exists(candidate))
        {
            return Path.GetExtension(candidate).Equals(".php", StringComparison.OrdinalIgnoreCase)
                ? new ResolvedResource(null, candidate, requestPath, null)
                : new ResolvedResource(candidate, null, null, null);
        }

        var frontController = Path.Combine(root, "index.php");
        if (!File.Exists(frontController)) throw new HttpRequestException("404 Not Found");
        return new ResolvedResource(
            null,
            frontController,
            "/index.php",
            requestPath == "/" ? null : requestPath
        );
    }

    private static async Task<HttpRequestData> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        using var received = new MemoryStream();
        var buffer = new byte[16 * 1_024];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) throw new HttpRequestException("400 Bad Request");
            received.Write(buffer, 0, count);
            if (received.Length > MaximumHeaderSize) throw new HttpRequestException("431 Request Header Fields Too Large");
            headerEnd = IndexOf(received.GetBuffer().AsSpan(0, (int)received.Length), "\r\n\r\n"u8);
        }

        var data = received.ToArray();
        var bodyOffset = headerEnd + 4;
        var headerText = Encoding.UTF8.GetString(data, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/", StringComparison.Ordinal))
        {
            throw new HttpRequestException("400 Bad Request");
        }
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) throw new HttpRequestException("400 Bad Request");
            headers.Add(new KeyValuePair<string, string>(
                line[..separator].Trim(),
                line[(separator + 1)..].Trim()
            ));
        }
        var chunked = headers.Any(header =>
            header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            && header.Value.Split(',').Any(value => value.Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase)));
        var unsupportedTransferCoding = headers
            .Where(header => header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value.Split(','))
            .Select(value => value.Trim())
            .Any(value => !value.Equals("chunked", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("identity", StringComparison.OrdinalIgnoreCase));
        if (unsupportedTransferCoding)
        {
            throw new HttpRequestException("501 Not Implemented");
        }
        var contentLengthText = headers.FirstOrDefault(header =>
            header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        ).Value;
        if (chunked && !string.IsNullOrEmpty(contentLengthText))
        {
            throw new HttpRequestException("400 Bad Request");
        }
        if (!string.IsNullOrEmpty(contentLengthText)
            && !int.TryParse(contentLengthText, out _))
        {
            throw new HttpRequestException("400 Bad Request");
        }
        var contentLength = string.IsNullOrEmpty(contentLengthText) ? 0 : int.Parse(contentLengthText);
        if (contentLength < 0 || contentLength > MaximumBodySize)
        {
            throw new HttpRequestException("413 Payload Too Large");
        }
        byte[] body;
        if (chunked)
        {
            var reader = new RequestBodyReader(data.AsMemory(bodyOffset), stream);
            body = await ReadChunkedBodyAsync(reader, cancellationToken);
        }
        else
        {
            body = new byte[contentLength];
        }
        var bufferedBody = Math.Min(contentLength, data.Length - bodyOffset);
        if (!chunked && bufferedBody > 0) Array.Copy(data, bodyOffset, body, 0, bufferedBody);
        var offset = bufferedBody;
        while (!chunked && offset < body.Length)
        {
            var count = await stream.ReadAsync(body.AsMemory(offset), cancellationToken);
            if (count == 0) throw new HttpRequestException("400 Bad Request");
            offset += count;
        }
        return new HttpRequestData(
            requestLine[0].ToUpperInvariant(),
            requestLine[1],
            requestLine[2],
            headers,
            body
        );
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(
        RequestBodyReader reader,
        CancellationToken cancellationToken
    )
    {
        using var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await reader.ReadLineAsync(8_192, cancellationToken);
            var separator = sizeLine.IndexOf(';');
            var sizeText = (separator >= 0 ? sizeLine[..separator] : sizeLine).Trim();
            if (sizeText.Length == 0
                || !int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size)
                || size < 0)
            {
                throw new HttpRequestException("400 Bad Request");
            }
            if (size == 0)
            {
                var trailerSize = 0;
                while (true)
                {
                    var trailer = await reader.ReadLineAsync(8_192, cancellationToken);
                    if (trailer.Length == 0) return body.ToArray();
                    trailerSize += trailer.Length + 2;
                    if (trailerSize > MaximumHeaderSize || trailer.IndexOf(':') <= 0)
                    {
                        throw new HttpRequestException("400 Bad Request");
                    }
                }
            }
            if (body.Length + size > MaximumBodySize)
            {
                throw new HttpRequestException("413 Payload Too Large");
            }
            await reader.CopyExactlyAsync(body, size, cancellationToken);
            if (await reader.ReadByteAsync(cancellationToken) != '\r'
                || await reader.ReadByteAsync(cancellationToken) != '\n')
            {
                throw new HttpRequestException("400 Bad Request");
            }
        }
    }

    private static byte[] ParseFastCgiResponse(byte[] response)
    {
        var delimiter = IndexOf(response, "\r\n\r\n"u8);
        var delimiterLength = 4;
        if (delimiter < 0)
        {
            delimiter = IndexOf(response, "\n\n"u8);
            delimiterLength = 2;
        }
        if (delimiter < 0) throw new InvalidDataException("PHP returned invalid CGI headers.");

        var headerText = Encoding.UTF8.GetString(response, 0, delimiter).Replace("\r\n", "\n");
        var body = response[(delimiter + delimiterLength)..];
        var status = "200 OK";
        var headers = new List<(string, string)>();
        foreach (var line in headerText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("Status", StringComparison.OrdinalIgnoreCase)) status = value;
            else if (!name.Equals("Connection", StringComparison.OrdinalIgnoreCase)) headers.Add((name, value));
        }
        if (status == "200 OK" && headers.Any(header =>
            header.Item1.Equals("Location", StringComparison.OrdinalIgnoreCase))) status = "302 Found";
        if (!headers.Any(header => header.Item1.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
            headers.Add(("Content-Length", body.Length.ToString()));
        if (!headers.Any(header => header.Item1.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            headers.Add(("Content-Type", "text/html; charset=utf-8"));
        headers.Add(("Connection", "close"));
        return MakeResponse(status, headers, body);
    }

    private static byte[] MakeResponse(string status, IEnumerable<(string, string)> headers, byte[] body)
    {
        using var output = new MemoryStream();
        var head = new StringBuilder($"HTTP/1.1 {status}\r\n");
        foreach (var header in headers) head.Append(header.Item1).Append(": ").Append(header.Item2).Append("\r\n");
        head.Append("\r\n");
        output.Write(Encoding.UTF8.GetBytes(head.ToString()));
        output.Write(body);
        return output.ToArray();
    }

    private static Task WriteErrorAsync(Stream stream, string status, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(ErrorResponse(status), cancellationToken).AsTask();
    }

    private static byte[] ErrorResponse(string status)
    {
        var body = Encoding.UTF8.GetBytes("HerdMe could not serve this local site.\n");
        return MakeResponse(
            status,
            [
                ("Content-Type", "text/plain; charset=utf-8"),
                ("Content-Length", body.Length.ToString()),
                ("Connection", "close")
            ],
            body
        );
    }

    private static string DocumentRoot(string sitePath)
    {
        var publicPath = Path.Combine(sitePath, "public");
        return Directory.Exists(publicPath) ? Path.GetFullPath(publicPath) : Path.GetFullPath(sitePath);
    }

    private static string NormalizeHost(string host)
    {
        return host.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static int AvailablePort(int preferredPort, int fallbackPort)
    {
        if (CanListen(preferredPort)) return preferredPort;
        for (var port = fallbackPort; port < fallbackPort + 100; port++)
        {
            if (port != preferredPort && CanListen(port)) return port;
        }
        throw new InvalidOperationException("No local HTTP port is available.");
    }

    private static bool CanListen(int port)
    {
        if (port is <= 0 or > 65_535) return false;
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value)
    {
        return data.IndexOf(value);
    }

    private static void WritePhpLog(byte[] errors)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HerdMe",
                "Log",
                "sites"
            );
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "php-errors.log"),
                Encoding.UTF8.GetString(errors)
            );
        }
        catch (IOException)
        {
        }
    }

    private sealed record HttpRequestData(
        string Method,
        string Target,
        string Protocol,
        IReadOnlyList<KeyValuePair<string, string>> Headers,
        byte[] Body
    )
    {
        public string? Header(string name) => Headers.FirstOrDefault(header =>
            header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
        ).Value;
    }

    private sealed class RequestBodyReader
    {
        private readonly ReadOnlyMemory<byte> buffered;
        private readonly Stream stream;
        private int offset;

        public RequestBodyReader(ReadOnlyMemory<byte> buffered, Stream stream)
        {
            this.buffered = buffered;
            this.stream = stream;
        }

        public async Task<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (offset < buffered.Length) return buffered.Span[offset++];
            var one = new byte[1];
            var count = await stream.ReadAsync(one, cancellationToken);
            if (count == 0) throw new HttpRequestException("400 Bad Request");
            return one[0];
        }

        public async Task<string> ReadLineAsync(int maximumLength, CancellationToken cancellationToken)
        {
            using var line = new MemoryStream();
            while (true)
            {
                var value = await ReadByteAsync(cancellationToken);
                if (value == '\r')
                {
                    if (await ReadByteAsync(cancellationToken) != '\n')
                    {
                        throw new HttpRequestException("400 Bad Request");
                    }
                    return Encoding.ASCII.GetString(line.ToArray());
                }
                line.WriteByte((byte)value);
                if (line.Length > maximumLength) throw new HttpRequestException("400 Bad Request");
            }
        }

        public async Task CopyExactlyAsync(
            Stream destination,
            int count,
            CancellationToken cancellationToken
        )
        {
            if (offset < buffered.Length)
            {
                var available = Math.Min(count, buffered.Length - offset);
                await destination.WriteAsync(buffered.Slice(offset, available), cancellationToken);
                offset += available;
                count -= available;
            }
            var transfer = new byte[Math.Min(16 * 1_024, Math.Max(1, count))];
            while (count > 0)
            {
                var read = await stream.ReadAsync(transfer.AsMemory(0, Math.Min(count, transfer.Length)), cancellationToken);
                if (read == 0) throw new HttpRequestException("400 Bad Request");
                await destination.WriteAsync(transfer.AsMemory(0, read), cancellationToken);
                count -= read;
            }
        }
    }

    private sealed record RequestTarget(string Path, string Query)
    {
        public static RequestTarget Parse(string target)
        {
            var raw = RawPathAndQuery(target);
            var separator = raw.IndexOf('?');
            var encodedPath = separator >= 0 ? raw[..separator] : raw;
            var query = separator >= 0 ? raw[(separator + 1)..] : string.Empty;
            string path;
            try
            {
                path = Uri.UnescapeDataString(encodedPath);
            }
            catch (UriFormatException)
            {
                throw new HttpRequestException("400 Bad Request");
            }
            if (!path.StartsWith('/')) throw new HttpRequestException("400 Bad Request");
            return new RequestTarget(string.IsNullOrEmpty(path) ? "/" : path, query);
        }

        private static string RawPathAndQuery(string target)
        {
            if (target.StartsWith('/')) return target;
            if (!Uri.TryCreate(target, UriKind.Absolute, out var absolute)
                || absolute.Scheme is not ("http" or "https")
                || target.Contains('#'))
            {
                throw new HttpRequestException("400 Bad Request");
            }

            var authorityStart = target.IndexOf("://", StringComparison.Ordinal) + 3;
            var pathStart = target.IndexOfAny(['/', '?'], authorityStart);
            if (pathStart < 0) return "/";
            return target[pathStart] == '?' ? "/" + target[pathStart..] : target[pathStart..];
        }
    }

    private sealed record ResolvedResource(
        string? StaticFile,
        string? ScriptFile,
        string? ScriptName,
        string? PathInfo
    );

    private sealed record SiteRoute(string DocumentRoot, int PhpFastCgiPort);

    private sealed class HttpRequestException : Exception
    {
        public HttpRequestException(string status) : base(status)
        {
            Status = status;
        }

        public string Status { get; }
    }

    private static string MimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".xml" => "application/xml; charset=utf-8",
        ".txt" or ".log" => "text/plain; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };
}
