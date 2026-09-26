using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HerdMe.Windows.Services;

public sealed record LocalSiteDefinition(
    string Domain,
    string Path,
    int? PhpFastCgiPort = null,
    bool PhpUsesHttpFallback = false,
    int? DevelopmentServerPort = null
);

public sealed record SiteRequestMetric(
    DateTimeOffset Timestamp,
    string Method,
    string Target,
    int StatusCode,
    TimeSpan Duration
);

public sealed record SitePerformanceSnapshot(
    long RequestCount,
    long ServerErrorCount,
    int ActiveRequests,
    TimeSpan AverageDuration,
    TimeSpan SlowestDuration,
    DateTimeOffset? LastRequestAt,
    IReadOnlyList<SiteRequestMetric> RecentRequests
);

public sealed class LocalHttpSiteServer : IAsyncDisposable
{
    private const int MaximumHeaderSize = 1 * 1_024 * 1_024;
    private const int MaximumBodySize = 32 * 1_024 * 1_024;
    private const int MaximumPersistentRequests = 100;
    private static readonly TimeSpan PersistentIdleTimeout = TimeSpan.FromSeconds(5);
    // Request bodies (uploads) are not bound by the 5s header/idle window; instead the
    // connection is dropped only if the client stops sending body bytes for this long.
    private static readonly TimeSpan RequestBodyIdleTimeout = TimeSpan.FromSeconds(30);
    private readonly FastCgiClient fastCgiClient = new();
    private static readonly TimeSpan TlsHandshakeTimeout = TimeSpan.FromSeconds(10);
    // Requests paused on an Xdebug breakpoint can legitimately take minutes, so the
    // proxy must never impose HttpClient's default 100 second limit.
    private readonly HttpClient phpHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly ConcurrentDictionary<int, Task> sessions = new();
    private readonly SemaphoreSlim sessionGate = new(128, 128);
    private const long PathCacheLifetimeMilliseconds = 2_000;
    private static readonly ConcurrentDictionary<string, CachedPathCheck> FrontControllerCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CachedPathCheck> RootCanonicalCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SitePerformanceBucket> performance =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? cancellation;
    private TcpListener? listener;
    private Task? acceptTask;
    private IReadOnlyDictionary<string, SiteRoute> routes = new Dictionary<string, SiteRoute>();
    private readonly ConcurrentDictionary<string, string> shareAliases = new(StringComparer.OrdinalIgnoreCase);
    private int fastCgiPort;
    private int sessionIdentifier;
    private X509Certificate2? certificate;

    public int? Port { get; private set; }

    /// <summary>
    /// Routes a public tunnel host name (for example abc.trycloudflare.com) to an existing
    /// local site. The tunnel terminates HTTPS, so PHP sees an https request for that host
    /// and generates links that work for the visitor. The listener itself stays loopback-only.
    /// </summary>
    public void SetShareAlias(string publicHost, string domain)
    {
        var alias = NormalizeHost(publicHost);
        var target = NormalizeHost(domain);
        if (alias.Length == 0 || target.Length == 0 || routes.ContainsKey(alias))
        {
            throw new ArgumentException("The share host name is not valid.", nameof(publicHost));
        }
        shareAliases[alias] = target;
    }

    public void RemoveShareAlias(string publicHost)
    {
        shareAliases.TryRemove(NormalizeHost(publicHost), out _);
    }

    public void RemoveShareAliasesFor(string domain)
    {
        var target = NormalizeHost(domain);
        foreach (var alias in shareAliases.Where(entry => entry.Value == target).Select(entry => entry.Key).ToArray())
        {
            shareAliases.TryRemove(alias, out _);
        }
    }

    public bool IsRunning => listener is not null && acceptTask is { IsCompleted: false };

    public SitePerformanceSnapshot Performance(string domain)
    {
        return performance.TryGetValue(NormalizeHost(domain), out var bucket)
            ? bucket.Snapshot()
            : EmptyPerformance();
    }

    public void ResetPerformance(string domain)
    {
        performance.TryRemove(NormalizeHost(domain), out _);
    }

    public Task<int> StartAsync(
        IEnumerable<LocalSiteDefinition> sites,
        int phpFastCgiPort,
        int preferredPort = 80,
        int? fallbackPort = 8_080,
        X509Certificate2? serverCertificate = null,
        CancellationToken cancellationToken = default
    )
    {
        if (IsRunning && Port is not null) return Task.FromResult(Port.Value);
        var normalized = sites.ToDictionary(
            site => NormalizeHost(site.Domain),
            site => new SiteRoute(
                // Proxy sites have no folder: every request goes to the loopback target.
                string.IsNullOrEmpty(site.Path) && site.DevelopmentServerPort is not null
                    ? string.Empty
                    : DocumentRoot(site.Path),
                site.PhpFastCgiPort ?? phpFastCgiPort,
                site.PhpUsesHttpFallback,
                site.DevelopmentServerPort
            ),
            StringComparer.OrdinalIgnoreCase
        );
        if (normalized.Count == 0) throw new InvalidOperationException("No local sites were provided.");

        var selectedPort = AvailablePort(preferredPort, fallbackPort);
        FrontControllerCache.Clear();
        RootCanonicalCache.Clear();
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
        await QueuedLog.FlushAllAsync();
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
            TcpClient client;
            try
            {
                client = await activeListener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (SocketException) when (!cancellationToken.IsCancellationRequested)
            {
                // A browser that resets a queued connection must not stop every site.
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
                continue;
            }
            await sessionGate.WaitAsync(cancellationToken);
            var identifier = Interlocked.Increment(ref sessionIdentifier);
            var task = RunClientAsync(client, cancellationToken);
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

    private async Task RunClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try { await HandleClientAsync(client, cancellationToken); }
        finally { sessionGate.Release(); }
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
                    using var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken
                    );
                    // Idle or half-open connections must not hold one of the 128 session slots.
                    handshakeCancellation.CancelAfter(TlsHandshakeTimeout);
                    try
                    {
                        await secureStream.AuthenticateAsServerAsync(
                            new SslServerAuthenticationOptions
                            {
                                ServerCertificate = certificate,
                                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                                ClientCertificateRequired = false
                            },
                            handshakeCancellation.Token
                        );
                    }
                    catch (Exception error) when (
                        error is AuthenticationException
                            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    )
                    {
                        secureStream.Dispose();
                        secureStream = null;
                        return;
                    }
                    stream = secureStream;
                }
                var reader = new HttpRequestReader(stream);
                for (var requestCount = 0; requestCount < MaximumPersistentRequests; requestCount++)
                {
                    request = null;
                    using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken
                    );
                    requestCancellation.CancelAfter(PersistentIdleTimeout);
                    try
                    {
                        request = await reader.ReadAsync(
                            allowCleanEndOfStream: requestCount > 0,
                            requestCancellation
                        );
                    }
                    catch (OperationCanceledException) when (
                        requestCount > 0 && !cancellationToken.IsCancellationRequested
                    )
                    {
                        return;
                    }
                    if (request is null) return;

                    var host = request.Header("Host")?.Split(':', 2)[0];
                    var normalizedHost = host is null ? null : NormalizeHost(host);
                    SiteRoute? route = null;
                    if (normalizedHost is not null && !routes.TryGetValue(normalizedHost, out route)
                        && shareAliases.TryGetValue(normalizedHost, out var sharedDomain)
                        && routes.TryGetValue(sharedDomain, out var sharedRoute))
                    {
                        route = sharedRoute with { Shared = true };
                        normalizedHost = sharedDomain;
                    }
                    if (normalizedHost is null || route is null)
                    {
                        await WriteErrorAsync(stream, "404 Not Found", cancellationToken);
                        return;
                    }
                    var keepAlive = request.AllowsPersistentConnection
                        && requestCount + 1 < MaximumPersistentRequests;
                    var bucket = performance.GetOrAdd(normalizedHost, _ => new SitePerformanceBucket());
                    var startedAt = Stopwatch.GetTimestamp();
                    bucket.Begin();
                    try
                    {
                        if (route.DevelopmentServerPort is { } developmentPort
                            && request.Header("Upgrade")?.Equals(
                                "websocket",
                                StringComparison.OrdinalIgnoreCase
                            ) == true)
                        {
                            await ProxyWebSocketAsync(
                                stream,
                                request,
                                developmentPort,
                                cancellationToken
                            );
                            bucket.Complete(
                                request.Method,
                                request.Target,
                                101,
                                Stopwatch.GetElapsedTime(startedAt)
                            );
                            return;
                        }
                        var response = await WriteResponseAsync(
                            stream,
                            request,
                            route,
                            keepAlive,
                            cancellationToken
                        );
                        bucket.Complete(
                            request.Method,
                            request.Target,
                            response.StatusCode,
                            Stopwatch.GetElapsedTime(startedAt)
                        );
                        if (!response.KeepAlive) return;
                    }
                    catch
                    {
                        bucket.Complete(
                            request.Method,
                            request.Target,
                            502,
                            Stopwatch.GetElapsedTime(startedAt)
                        );
                        throw;
                    }
                }
            }
            catch (HttpStatusException error)
            {
                await WriteErrorAsync(stream, error.Status, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (HttpResponseStartedException)
            {
            }
            catch (Exception error) when (error is IOException or SocketException or InvalidDataException
                or System.Net.Http.HttpRequestException)
            {
                // Includes a PHP HTTP fallback or Vite dev server that refused or dropped
                // the proxied request: the browser gets a real 502 page instead of a reset.
                if (request is not null)
                {
                    await WriteErrorAsync(stream, "502 Bad Gateway", cancellationToken);
                }
            }
            catch (TimeoutException) when (request is not null)
            {
                await WriteErrorAsync(stream, "504 Gateway Timeout", cancellationToken);
            }
            finally
            {
                if (secureStream is not null)
                {
                    try
                    {
                        await secureStream.ShutdownAsync();
                    }
                    catch (Exception error) when (
                        error is AuthenticationException
                            or IOException
                            or InvalidOperationException
                            or SocketException
                    )
                    {
                    }
                    secureStream.Dispose();
                }
            }
        }
    }

    private async Task<LocalResponseResult> WriteResponseAsync(
        Stream destination,
        HttpRequestData request,
        SiteRoute route,
        bool keepAlive,
        CancellationToken cancellationToken
    )
    {
        var target = RequestTarget.Parse(request.Target);
        var secure = certificate is not null || route.Shared;
        if (route.DevelopmentServerPort is { } developmentPort)
        {
            return await WriteHttpProxyAsync(
                destination,
                request,
                developmentPort,
                keepAlive,
                secure,
                cancellationToken
            );
        }
        var resource = Resolve(route.DocumentRoot, target.Path);
        if (resource.StaticFile is not null)
        {
            if (request.Method is not ("GET" or "HEAD"))
            {
                await destination.WriteAsync(ErrorResponse("405 Method Not Allowed"), cancellationToken);
                return new LocalResponseResult(false, 405);
            }
            return await WriteStaticFileAsync(
                destination,
                route.DocumentRoot,
                resource.StaticFile,
                request,
                keepAlive,
                cancellationToken
            );
        }

        if (route.PhpUsesHttpFallback)
        {
            return await WritePhpHttpFallbackAsync(
                destination,
                request,
                route,
                keepAlive,
                cancellationToken
            );
        }

        var parameters = FastCgiParameters(
            request,
            target,
            route.DocumentRoot,
            resource,
            secure
        );
        var writer = new FastCgiHttpResponseWriter(
            destination,
            request.Method == "HEAD",
            keepAlive,
            allowChunkedEncoding: request.Protocol == "HTTP/1.1"
        );
        try
        {
            var result = await fastCgiClient.PerformStreamingAsync(
                route.PhpFastCgiPort,
                parameters,
                request.Body,
                writer.WriteAsync,
                cancellationToken
            );
            await writer.CompleteAsync(cancellationToken);
            if (result.StandardError.Length > 0) WritePhpLog(result.StandardError);
            return new LocalResponseResult(writer.KeepsConnectionAlive, writer.StatusCode);
        }
        catch (Exception error) when (writer.HasStarted && error is not OperationCanceledException)
        {
            throw new HttpResponseStartedException(error);
        }
    }

    private async Task<LocalResponseResult> WritePhpHttpFallbackAsync(
        Stream destination,
        HttpRequestData request,
        SiteRoute route,
        bool keepAlive,
        CancellationToken cancellationToken
    )
    {
        using var upstream = new HttpRequestMessage(
            new HttpMethod(request.Method),
            $"http://127.0.0.1:{route.PhpFastCgiPort}{request.Target}"
        );
        var host = request.Header("Host")?.Split(':', 2)[0] ?? "localhost";
        upstream.Headers.Host = host;
        var scheme = certificate is not null || route.Shared ? "https" : "http";
        upstream.Headers.TryAddWithoutValidation("X-Forwarded-Proto", scheme);
        upstream.Headers.TryAddWithoutValidation("X-HerdMe-Original-Scheme", scheme);
        upstream.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");
        if (request.Body.Length > 0)
        {
            upstream.Content = new ByteArrayContent(request.Body);
        }
        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (!(upstream.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value) ?? false))
            {
                upstream.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        using var response = await phpHttpClient.SendAsync(
            upstream,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (body.Length > MaximumBodySize)
        {
            throw new InvalidDataException("PHP HTTP fallback response exceeded the limit.");
        }
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => !header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value.Select(value => (header.Key, value)))
            .ToList();
        headers.RemoveAll(header => header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase));
        headers.Add(("Content-Length", body.Length.ToString(CultureInfo.InvariantCulture)));
        headers.Add(("Connection", keepAlive ? "keep-alive" : "close"));
        var reason = response.ReasonPhrase ?? string.Empty;
        await destination.WriteAsync(
            MakeResponseHead($"{(int)response.StatusCode} {reason}".TrimEnd(), headers),
            cancellationToken
        );
        if (request.Method != "HEAD" && body.Length > 0)
        {
            await destination.WriteAsync(body, cancellationToken);
        }
        return new LocalResponseResult(keepAlive, (int)response.StatusCode);
    }

    private async Task<LocalResponseResult> WriteHttpProxyAsync(
        Stream destination,
        HttpRequestData request,
        int upstreamPort,
        bool keepAlive,
        bool secure,
        CancellationToken cancellationToken
    )
    {
        using var upstream = new HttpRequestMessage(
            new HttpMethod(request.Method),
            $"http://127.0.0.1:{upstreamPort}{request.Target}"
        );
        // Development servers (Vite, Nuxt, Next) reject unknown Host headers, so the
        // upstream sees its own loopback address and the original name in X-Forwarded-Host.
        upstream.Headers.Host = $"127.0.0.1:{upstreamPort}";
        upstream.Headers.TryAddWithoutValidation(
            "X-Forwarded-Host",
            request.Header("Host")?.Split(':', 2)[0] ?? "localhost"
        );
        upstream.Headers.TryAddWithoutValidation("X-Forwarded-Proto", secure ? "https" : "http");
        upstream.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");
        if (request.Body.Length > 0) upstream.Content = new ByteArrayContent(request.Body);
        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
                || header.Key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)) continue;
            if (!(upstream.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value) ?? false))
                upstream.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        using var response = await phpHttpClient.SendAsync(
            upstream,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        var contentLength = response.Content.Headers.ContentLength;
        var headOnly = request.Method == "HEAD";
        // Streamed responses (server-sent events, React streaming, large downloads) are
        // forwarded as they arrive instead of being buffered until the upstream finishes.
        var chunked = contentLength is null && !headOnly && request.Protocol == "HTTP/1.1";
        var persistent = keepAlive && (contentLength is not null || chunked || headOnly);
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(header => !header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value.Select(value => (header.Key, value)))
            .ToList();
        if (contentLength is { } length)
        {
            headers.Add(("Content-Length", length.ToString(CultureInfo.InvariantCulture)));
        }
        else if (chunked)
        {
            headers.Add(("Transfer-Encoding", "chunked"));
        }
        headers.Add(("Connection", persistent ? "keep-alive" : "close"));
        await destination.WriteAsync(
            MakeResponseHead(
                $"{(int)response.StatusCode} {response.ReasonPhrase ?? string.Empty}".TrimEnd(),
                headers
            ),
            cancellationToken
        );
        if (headOnly) return new LocalResponseResult(persistent, (int)response.StatusCode);
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
            try
            {
                long remaining = contentLength ?? long.MaxValue;
                while (remaining > 0)
                {
                    var read = await body.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                        cancellationToken
                    );
                    if (read == 0) break;
                    remaining -= read;
                    if (chunked)
                    {
                        await destination.WriteAsync(
                            Encoding.ASCII.GetBytes(read.ToString("X", CultureInfo.InvariantCulture) + "\r\n"),
                            cancellationToken
                        );
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        await destination.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
                    }
                    else
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    // Flush every read so event streams reach the browser immediately.
                    await destination.FlushAsync(cancellationToken);
                }
                if (contentLength is not null && remaining > 0)
                {
                    throw new IOException("The development server closed the response early.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            if (chunked) await destination.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new HttpResponseStartedException(error);
        }
        return new LocalResponseResult(persistent, (int)response.StatusCode);
    }

    private static async Task ProxyWebSocketAsync(
        Stream browserStream,
        HttpRequestData request,
        int upstreamPort,
        CancellationToken cancellationToken
    )
    {
        using var upstreamClient = new TcpClient { NoDelay = true };
        await upstreamClient.ConnectAsync(IPAddress.Loopback, upstreamPort, cancellationToken);
        await using var upstream = upstreamClient.GetStream();
        var head = new StringBuilder()
            .Append(request.Method).Append(' ')
            .Append(request.Target).Append(' ')
            .Append(request.Protocol).Append("\r\n")
            .Append("Host: 127.0.0.1:").Append(upstreamPort).Append("\r\n");
        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }
        head.Append("\r\n");
        await upstream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken);
        using var tunnelCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var browserToUpstream = browserStream.CopyToAsync(upstream, tunnelCancellation.Token);
        var upstreamToBrowser = upstream.CopyToAsync(browserStream, tunnelCancellation.Token);
        await Task.WhenAny(browserToUpstream, upstreamToBrowser);
        tunnelCancellation.Cancel();
        try { await Task.WhenAll(browserToUpstream, upstreamToBrowser); }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
    }

    private static async Task<LocalResponseResult> WriteStaticFileAsync(
        Stream destination,
        string documentRoot,
        string path,
        HttpRequestData request,
        bool keepAlive,
        CancellationToken cancellationToken
    )
    {
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1_024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        if (!IsInside(FinalPath(file.SafeFileHandle, file.Name), documentRoot))
        {
            throw new HttpStatusException("403 Forbidden");
        }
        var fileSize = file.Length;
        var lastWriteUtc = File.GetLastWriteTimeUtc(file.SafeFileHandle);
        var lastModified = TruncateToSeconds(lastWriteUtc);
        var entityTag = StaticEntityTag(fileSize, lastWriteUtc);
        var lastModifiedText = lastModified.ToString("R", CultureInfo.InvariantCulture);
        if (IsNotModified(request, entityTag, lastModified))
        {
            await destination.WriteAsync(
                MakeResponseHead(
                    "304 Not Modified",
                    [
                        ("ETag", entityTag),
                        ("Last-Modified", lastModifiedText),
                        ("Cache-Control", "no-cache"),
                        ("Connection", keepAlive ? "keep-alive" : "close")
                    ]
                ),
                cancellationToken
            );
            return new LocalResponseResult(keepAlive, 304);
        }
        var rangeHeader = RangeValidatorMatches(request.Header("If-Range"), entityTag, lastModified)
            ? request.Header("Range")
            : null;
        if (!TrySelectByteRange(rangeHeader, fileSize, out var selectedRange))
        {
            await destination.WriteAsync(
                MakeResponseHead(
                    "416 Range Not Satisfiable",
                    [
                        ("Content-Range", $"bytes */{fileSize}"),
                        ("Accept-Ranges", "bytes"),
                        ("Content-Length", "0"),
                        ("Cache-Control", "no-cache"),
                        ("Connection", keepAlive ? "keep-alive" : "close")
                    ]
                ),
                cancellationToken
            );
            return new LocalResponseResult(keepAlive, 416);
        }

        var headers = new List<(string, string)>
        {
            ("Content-Type", MimeType(Path.GetExtension(path))),
            ("Content-Length", selectedRange.Length.ToString()),
            ("Accept-Ranges", "bytes"),
            ("Cache-Control", "no-cache"),
            ("ETag", entityTag),
            ("Last-Modified", lastModifiedText),
            ("Connection", keepAlive ? "keep-alive" : "close")
        };
        if (selectedRange.IsPartial)
        {
            headers.Insert(
                2,
                (
                    "Content-Range",
                    $"bytes {selectedRange.Offset}-{selectedRange.Offset + selectedRange.Length - 1}/{fileSize}"
                )
            );
        }
        await destination.WriteAsync(
            MakeResponseHead(selectedRange.IsPartial ? "206 Partial Content" : "200 OK", headers),
            cancellationToken
        );
        var statusCode = selectedRange.IsPartial ? 206 : 200;
        if (request.Method == "HEAD" || selectedRange.Length == 0)
        {
            return new LocalResponseResult(keepAlive, statusCode);
        }

        file.Seek(selectedRange.Offset, SeekOrigin.Begin);
        var buffer = new byte[64 * 1_024];
        var remaining = selectedRange.Length;
        while (remaining > 0)
        {
            var count = await file.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken
            );
            if (count == 0) throw new IOException("The static file changed while it was being served.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            remaining -= count;
        }
        return new LocalResponseResult(keepAlive, statusCode);
    }

    private static DateTime TruncateToSeconds(DateTime value)
    {
        return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    private static string StaticEntityTag(long fileSize, DateTime lastWriteUtc)
    {
        // Size plus full-precision write time: cheap to compute and changes on every save.
        return "\""
            + fileSize.ToString("x", CultureInfo.InvariantCulture)
            + "-"
            + lastWriteUtc.Ticks.ToString("x", CultureInfo.InvariantCulture)
            + "\"";
    }

    private static bool IsNotModified(HttpRequestData request, string entityTag, DateTime lastModified)
    {
        var ifNoneMatch = request.Header("If-None-Match");
        if (ifNoneMatch is not null)
        {
            // If-None-Match takes precedence over If-Modified-Since and uses weak comparison.
            return ifNoneMatch
                .Split(',')
                .Select(value => value.Trim())
                .Any(value => value == "*" || WithoutWeakPrefix(value) == entityTag);
        }
        var ifModifiedSince = request.Header("If-Modified-Since");
        return ifModifiedSince is not null
            && TryParseHttpDate(ifModifiedSince, out var since)
            && lastModified <= since;
    }

    private static bool RangeValidatorMatches(string? ifRange, string entityTag, DateTime lastModified)
    {
        if (ifRange is null) return true;
        var validator = ifRange.Trim();
        if (validator.StartsWith("W/", StringComparison.Ordinal)) return false;
        if (validator.StartsWith('"')) return validator == entityTag;
        return TryParseHttpDate(validator, out var date) && date == lastModified;
    }

    private static string WithoutWeakPrefix(string entityTag)
    {
        return entityTag.StartsWith("W/", StringComparison.Ordinal) ? entityTag[2..] : entityTag;
    }

    private static bool TryParseHttpDate(string value, out DateTime date)
    {
        return DateTime.TryParseExact(
            value.Trim(),
            "R",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out date
        );
    }

    private static bool TrySelectByteRange(string? value, long fileSize, out ByteRange selectedRange)
    {
        selectedRange = new ByteRange(0, fileSize, false);
        if (value is null) return true;
        if (fileSize <= 0) return false;

        var components = value.Split('=', 2, StringSplitOptions.None);
        if (components.Length != 2
            || !components[0].Trim().Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || components[1].Contains(','))
        {
            return false;
        }
        var bounds = components[1].Split('-', 2, StringSplitOptions.None);
        if (bounds.Length != 2) return false;
        var startText = bounds[0].Trim();
        var endText = bounds[1].Trim();
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, out var suffixLength) || suffixLength <= 0) return false;
            var length = Math.Min(suffixLength, fileSize);
            selectedRange = new ByteRange(fileSize - length, length, true);
            return true;
        }
        if (!long.TryParse(startText, out var start) || start < 0 || start >= fileSize) return false;
        long end;
        if (endText.Length == 0)
        {
            end = fileSize - 1;
        }
        else if (!long.TryParse(endText, out var requestedEnd) || requestedEnd < start)
        {
            return false;
        }
        else
        {
            end = Math.Min(requestedEnd, fileSize - 1);
        }
        selectedRange = new ByteRange(start, end - start + 1, true);
        return true;
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
            ["CONTENT_LENGTH"] = request.Body.Length.ToString(),
            ["VAR_DUMPER_FORMAT"] = "server",
            ["VAR_DUMPER_SERVER"] = "127.0.0.1:9912"
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
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(documentRoot));
        var relative = requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        // NTFS alternate data streams (index.php::$DATA) would otherwise be served as
        // static files and disclose PHP source. Paths with a colon never name a file
        // HerdMe serves directly; the front controller still receives them as routes.
        var namesFile = relative.IndexOf(':') < 0;
        var candidate = namesFile ? Path.GetFullPath(Path.Combine(root, relative)) : root;
        if (!IsInside(candidate, root)) throw new HttpStatusException("403 Forbidden");

        if (namesFile && Directory.Exists(candidate))
        {
            var resolvedDirectory = ResolveInside(candidate, root);
            // PHP front controllers must win over stale static placeholders in moved Laravel projects.
            foreach (var index in new[] { "index.php", "index.html", "index.htm" })
            {
                var file = Path.Combine(resolvedDirectory, index);
                if (File.Exists(file))
                {
                    var resolved = ResolveInside(file, root);
                    if (index.Equals("index.php", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = requestPath.EndsWith('/') ? requestPath + index : requestPath + "/" + index;
                        return new ResolvedResource(null, resolved, name, null);
                    }
                    return new ResolvedResource(resolved, null, null, null);
                }
            }
        }
        else if (namesFile && File.Exists(candidate))
        {
            var resolvedFile = ResolveInside(candidate, root);
            return Path.GetExtension(resolvedFile).Equals(".php", StringComparison.OrdinalIgnoreCase)
                ? new ResolvedResource(null, resolvedFile, requestPath, null)
                : new ResolvedResource(resolvedFile, null, null, null);
        }

        return new ResolvedResource(
            null,
            ResolveFrontController(root),
            "/index.php",
            requestPath == "/" ? null : requestPath
        );
    }

    // Most Laravel requests are routes that end at public/index.php. Remember its resolved
    // location (or absence) for a short time instead of walking every path component again.
    private static string ResolveFrontController(string root)
    {
        var now = Environment.TickCount64;
        if (FrontControllerCache.TryGetValue(root, out var cached)
            && now - cached.CheckedAt < PathCacheLifetimeMilliseconds)
        {
            return cached.ResolvedPath ?? throw new HttpStatusException("404 Not Found");
        }
        var frontController = Path.Combine(root, "index.php");
        // ResolveInside throws 403 for escapes; such results are never cached.
        var resolved = File.Exists(frontController) ? ResolveInside(frontController, root) : null;
        FrontControllerCache[root] = new CachedPathCheck(now, resolved);
        return resolved ?? throw new HttpStatusException("404 Not Found");
    }

    private static string ResolveInside(string path, string root)
    {
        try
        {
            var resolved = CanonicalExistingPathBelow(path, root);
            if (IsInside(resolved, root)) return resolved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        throw new HttpStatusException("403 Forbidden");
    }

    // Equivalent to CanonicalExistingPath(path) while the document root itself is still canonical
    // (it is re-verified at most every PathCacheLifetimeMilliseconds). Every component below the
    // root is still checked for reparse points on each request.
    private static string CanonicalExistingPathBelow(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsInside(fullPath, root) || !RootIsCanonical(root)) return CanonicalExistingPath(fullPath);
        var current = root;
        var relative = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
        foreach (var component in relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries
        ))
        {
            var next = Path.Combine(current, component);
            FileSystemInfo entry = Directory.Exists(next)
                ? new DirectoryInfo(next)
                : new FileInfo(next);
            var target = entry.ResolveLinkTarget(returnFinalTarget: true);
            current = target is null ? next : Path.GetFullPath(target.FullName);
        }
        return Path.GetFullPath(current);
    }

    private static bool RootIsCanonical(string root)
    {
        var now = Environment.TickCount64;
        if (RootCanonicalCache.TryGetValue(root, out var cached)
            && now - cached.CheckedAt < PathCacheLifetimeMilliseconds)
        {
            return cached.ResolvedPath is not null;
        }
        string? canonical;
        try
        {
            canonical = CanonicalExistingPath(root).Equals(root, StringComparison.OrdinalIgnoreCase)
                ? root
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            canonical = null;
        }
        RootCanonicalCache[root] = new CachedPathCheck(now, canonical);
        return canonical is not null;
    }

    private static string CanonicalExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new IOException("The local site path has no filesystem root.");
        var current = pathRoot;
        var relative = Path.GetRelativePath(pathRoot, fullPath);
        foreach (var component in relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries
        ))
        {
            var next = Path.Combine(current, component);
            FileSystemInfo entry = Directory.Exists(next)
                ? new DirectoryInfo(next)
                : new FileInfo(next);
            var target = entry.ResolveLinkTarget(returnFinalTarget: true);
            current = target is null ? next : Path.GetFullPath(target.FullName);
        }
        return Path.GetFullPath(current);
    }

    private static bool IsInside(string candidate, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            );
    }

    private static string FinalPath(SafeFileHandle handle, string fallbackPath)
    {
        if (!OperatingSystem.IsWindows()) return CanonicalExistingPath(fallbackPath);
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)capacity, 0);
            if (length == 0)
            {
                throw new IOException(
                    $"Windows could not resolve the opened static file (error {Marshal.GetLastWin32Error()})."
                );
            }
            if (length < capacity)
            {
                var value = buffer.ToString();
                if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    return @"\\" + value[8..];
                }
                return value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
                    ? value[4..]
                    : value;
            }
            capacity = checked((int)length + 1);
        }
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(
        HttpRequestReader reader,
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
                throw new HttpStatusException("400 Bad Request");
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
                        throw new HttpStatusException("400 Bad Request");
                    }
                }
            }
            if (body.Length + size > MaximumBodySize)
            {
                throw new HttpStatusException("413 Payload Too Large");
            }
            await reader.CopyExactlyAsync(body, size, cancellationToken);
            if (await reader.ReadByteAsync(cancellationToken) != '\r'
                || await reader.ReadByteAsync(cancellationToken) != '\n')
            {
                throw new HttpStatusException("400 Bad Request");
            }
        }
    }

    private static ParsedFastCgiHead ParseFastCgiResponseHead(
        ReadOnlySpan<byte> response,
        bool allowKeepAlive,
        bool headOnly,
        bool allowChunkedEncoding
    )
    {
        string headerText;
        try
        {
            headerText = new UTF8Encoding(false, true).GetString(response).Replace("\r\n", "\n");
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("PHP returned non-UTF-8 CGI headers.", error);
        }
        var status = "200 OK";
        var headers = new List<(string, string)>();
        long? contentLength = null;
        var hopByHopHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
            "TE", "Trailer", "Transfer-Encoding", "Upgrade"
        };
        foreach (var line in headerText.Split('\n', StringSplitOptions.None))
        {
            if (line.Length == 0) continue;
            var separator = line.IndexOf(':');
            if (separator <= 0) throw new InvalidDataException("PHP returned malformed CGI headers.");
            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!IsValidHeaderName(name) || !IsValidHeaderValue(value))
            {
                throw new InvalidDataException("PHP returned unsafe CGI headers.");
            }
            if (name.Equals("Status", StringComparison.OrdinalIgnoreCase))
            {
                status = value;
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (contentLength is not null
                    || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    || parsed < 0)
                {
                    throw new InvalidDataException("PHP returned an invalid Content-Length header.");
                }
                contentLength = parsed;
                headers.Add((name, value));
            }
            else if (!hopByHopHeaders.Contains(name))
            {
                headers.Add((name, value));
            }
        }
        if (!IsValidStatus(status)) throw new InvalidDataException("PHP returned an invalid CGI status.");
        if (status == "200 OK" && headers.Any(header =>
            header.Item1.Equals("Location", StringComparison.OrdinalIgnoreCase))) status = "302 Found";
        if (!headers.Any(header => header.Item1.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            headers.Add(("Content-Type", "text/html; charset=utf-8"));
        var statusCode = int.Parse(status.AsSpan(0, 3), CultureInfo.InvariantCulture);
        var bodyForbidden = statusCode is >= 100 and < 200 or 204 or 304;
        // Laravel and most PHP apps never send Content-Length. For HTTP/1.1 clients the body is
        // framed with chunked encoding so the connection (and its TLS session) can be reused.
        var chunked = allowKeepAlive
            && allowChunkedEncoding
            && contentLength is null
            && !bodyForbidden
            && !headOnly;
        var keepAlive = allowKeepAlive && (contentLength is not null || bodyForbidden || headOnly || chunked);
        if (chunked) headers.Add(("Transfer-Encoding", "chunked"));
        headers.Add(("Connection", keepAlive ? "keep-alive" : "close"));
        return new ParsedFastCgiHead(
            MakeResponseHead(status, headers),
            contentLength,
            bodyForbidden,
            keepAlive,
            chunked,
            statusCode
        );
    }

    private static bool IsValidStatus(string status)
    {
        if (status.Length < 3
            || !status.AsSpan(0, 3).ToString().All(char.IsAsciiDigit)
            || !int.TryParse(status.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            || code is < 100 or > 599)
        {
            return false;
        }
        return status.Length == 3
            || char.IsWhiteSpace(status[3]) && IsValidHeaderValue(status);
    }

    private static bool IsValidHeaderName(string name)
    {
        return name.Length > 0 && name.All(character =>
            char.IsAsciiLetterOrDigit(character) || character == '-'
        );
    }

    private static bool IsValidHeaderValue(string value)
    {
        return value.All(character => character == '\t' || character is >= ' ' and <= '~');
    }

    private static byte[] MakeResponse(string status, IEnumerable<(string, string)> headers, byte[] body)
    {
        using var output = new MemoryStream();
        output.Write(MakeResponseHead(status, headers));
        output.Write(body);
        return output.ToArray();
    }

    private static byte[] MakeResponseHead(string status, IEnumerable<(string, string)> headers)
    {
        var head = new StringBuilder($"HTTP/1.1 {status}\r\n");
        foreach (var header in headers) head.Append(header.Item1).Append(": ").Append(header.Item2).Append("\r\n");
        head.Append("\r\n");
        return Encoding.UTF8.GetBytes(head.ToString());
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
        return CanonicalExistingPath(Directory.Exists(publicPath) ? publicPath : sitePath);
    }

    private static string NormalizeHost(string host)
    {
        return host.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static int AvailablePort(int preferredPort, int? fallbackPort)
    {
        if (CanListen(preferredPort)) return preferredPort;
        if (fallbackPort is null)
        {
            throw new InvalidOperationException(
                $"Local site port {preferredPort} is unavailable. Stop the application using this port, then retry."
            );
        }
        for (var port = fallbackPort.Value; port < fallbackPort.Value + 100; port++)
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
            // Queued so a request never waits for the log file or its rotation.
            QueuedLog.Append(
                Path.Combine(directory, "php-errors.log"),
                Encoding.UTF8.GetString(errors)
            );
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class FastCgiHttpResponseWriter
    {
        private static readonly byte[] HeaderDelimiter = "\r\n\r\n"u8.ToArray();
        private static readonly byte[] AlternateHeaderDelimiter = "\n\n"u8.ToArray();
        private static readonly byte[] LastChunk = "0\r\n\r\n"u8.ToArray();
        private readonly Stream destination;
        private readonly bool headOnly;
        private readonly bool allowKeepAlive;
        private readonly bool allowChunkedEncoding;
        private readonly MemoryStream headerBuffer = new();
        private long? declaredContentLength;
        private long bodyBytes;
        private bool bodyForbidden;
        private bool chunked;

        public FastCgiHttpResponseWriter(
            Stream destination,
            bool headOnly,
            bool allowKeepAlive,
            bool allowChunkedEncoding
        )
        {
            this.destination = destination;
            this.headOnly = headOnly;
            this.allowKeepAlive = allowKeepAlive;
            this.allowChunkedEncoding = allowChunkedEncoding;
        }

        public bool HasStarted { get; private set; }
        public bool KeepsConnectionAlive { get; private set; }
        public int StatusCode { get; private set; } = 200;

        public async ValueTask WriteAsync(
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken
        )
        {
            if (content.IsEmpty) return;
            if (HasStarted)
            {
                await WriteBodyAsync(content, cancellationToken);
                return;
            }

            await headerBuffer.WriteAsync(content, cancellationToken);
            var buffered = headerBuffer.ToArray();
            var delimiter = FindHeaderDelimiter(buffered);
            if (delimiter.Index < 0)
            {
                if (headerBuffer.Length > MaximumHeaderSize)
                {
                    throw new InvalidDataException("PHP returned CGI headers larger than 1MB.");
                }
                return;
            }
            if (delimiter.Index > MaximumHeaderSize)
            {
                throw new InvalidDataException("PHP returned CGI headers larger than 1MB.");
            }

            var parsed = ParseFastCgiResponseHead(
                buffered.AsSpan(0, delimiter.Index),
                allowKeepAlive,
                headOnly,
                allowChunkedEncoding
            );
            declaredContentLength = parsed.ContentLength;
            bodyForbidden = parsed.BodyForbidden;
            chunked = parsed.Chunked;
            KeepsConnectionAlive = parsed.KeepAlive;
            StatusCode = parsed.StatusCode;
            var bodyOffset = delimiter.Index + delimiter.Length;
            var bufferedBody = buffered.AsSpan(bodyOffset).ToArray();
            headerBuffer.SetLength(0);
            HasStarted = true;
            await destination.WriteAsync(parsed.ResponseHead, cancellationToken);
            await WriteBodyAsync(bufferedBody, cancellationToken);
        }

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (!HasStarted)
            {
                throw new InvalidDataException("PHP returned a FastCGI response without CGI headers.");
            }
            if (!headOnly
                && !bodyForbidden
                && declaredContentLength is { } expected
                && bodyBytes != expected)
            {
                throw new InvalidDataException("PHP returned a body that did not match Content-Length.");
            }
            if (chunked)
            {
                await destination.WriteAsync(LastChunk, cancellationToken);
            }
        }

        private async ValueTask WriteBodyAsync(
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken
        )
        {
            if (content.IsEmpty) return;
            if (content.Length > long.MaxValue - bodyBytes)
            {
                throw new InvalidDataException("PHP returned an oversized response body.");
            }
            bodyBytes += content.Length;
            if (declaredContentLength is { } expected && bodyBytes > expected)
            {
                throw new InvalidDataException("PHP returned a body larger than Content-Length.");
            }
            if (headOnly || bodyForbidden) return;
            if (chunked)
            {
                await WriteChunkAsync(content, cancellationToken);
                return;
            }
            await destination.WriteAsync(content, cancellationToken);
        }

        private async ValueTask WriteChunkAsync(
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken
        )
        {
            // One contiguous write per chunk keeps the size line, data, and CRLF in the same
            // TCP segment / TLS record instead of three tiny ones.
            var sizeLine = Encoding.ASCII.GetBytes(
                content.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n"
            );
            var frameLength = sizeLine.Length + content.Length + 2;
            var frame = ArrayPool<byte>.Shared.Rent(frameLength);
            try
            {
                sizeLine.CopyTo(frame, 0);
                content.Span.CopyTo(frame.AsSpan(sizeLine.Length));
                frame[frameLength - 2] = (byte)'\r';
                frame[frameLength - 1] = (byte)'\n';
                await destination.WriteAsync(frame.AsMemory(0, frameLength), cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame);
            }
        }

        private static (int Index, int Length) FindHeaderDelimiter(ReadOnlySpan<byte> content)
        {
            var standard = content.IndexOf(HeaderDelimiter);
            var alternate = content.IndexOf(AlternateHeaderDelimiter);
            if (standard < 0) return (alternate, alternate < 0 ? 0 : AlternateHeaderDelimiter.Length);
            if (alternate < 0 || standard <= alternate) return (standard, HeaderDelimiter.Length);
            return (alternate, AlternateHeaderDelimiter.Length);
        }
    }

    private sealed record ParsedFastCgiHead(
        byte[] ResponseHead,
        long? ContentLength,
        bool BodyForbidden,
        bool KeepAlive,
        bool Chunked,
        int StatusCode
    );

    private sealed class HttpResponseStartedException : Exception
    {
        public HttpResponseStartedException(Exception innerException)
            : base("The HTTP response had already started.", innerException)
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

        public bool AllowsPersistentConnection
        {
            get
            {
                var connectionTokens = Headers
                    .Where(header => header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(header => header.Value.Split(','))
                    .Select(value => value.Trim());
                if (connectionTokens.Any(value => value.Equals("close", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
                if (Protocol.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase)) return true;
                return Protocol.Equals("HTTP/1.0", StringComparison.OrdinalIgnoreCase)
                    && connectionTokens.Any(value => value.Equals("keep-alive", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private sealed class HttpRequestReader
    {
        private readonly Stream stream;
        private byte[] buffered = new byte[16 * 1_024];
        private int start;
        private int count;
        private CancellationTokenSource? bodyProgressTimeout;

        public HttpRequestReader(Stream stream)
        {
            this.stream = stream;
        }

        public async Task<HttpRequestData?> ReadAsync(
            bool allowCleanEndOfStream,
            CancellationTokenSource timeout
        )
        {
            var cancellationToken = timeout.Token;
            var headerEnd = FindHeaderEnd();
            while (headerEnd < 0)
            {
                if (count > MaximumHeaderSize)
                {
                    throw new HttpStatusException("431 Request Header Fields Too Large");
                }
                if (!await ReadMoreAsync(cancellationToken))
                {
                    if (allowCleanEndOfStream && count == 0) return null;
                    throw new HttpStatusException("400 Bad Request");
                }
                headerEnd = FindHeaderEnd();
            }
            if (headerEnd > MaximumHeaderSize)
            {
                throw new HttpStatusException("431 Request Header Fields Too Large");
            }

            string headerText;
            try
            {
                headerText = new UTF8Encoding(false, true).GetString(buffered, start, headerEnd);
            }
            catch (DecoderFallbackException error)
            {
                throw new HttpStatusException("400 Bad Request", error);
            }
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/", StringComparison.Ordinal))
            {
                throw new HttpStatusException("400 Bad Request");
            }
            if (requestLine[2] is not ("HTTP/1.0" or "HTTP/1.1"))
            {
                throw new HttpStatusException("505 HTTP Version Not Supported");
            }
            if (!IsValidHeaderName(requestLine[0])
                || requestLine[1].Any(character => character is <= ' ' or '\u007f'))
            {
                throw new HttpStatusException("400 Bad Request");
            }
            var headers = new List<KeyValuePair<string, string>>();
            foreach (var line in lines.Skip(1))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0) throw new HttpStatusException("400 Bad Request");
                var name = line[..separator];
                var value = line[(separator + 1)..].Trim();
                if (!IsValidHeaderName(name) || !IsValidHeaderValue(value))
                {
                    throw new HttpStatusException("400 Bad Request");
                }
                headers.Add(new KeyValuePair<string, string>(
                    name,
                    value
                ));
            }

            var hostHeaders = headers
                .Where(header => header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (hostHeaders.Length > 1
                || requestLine[2] == "HTTP/1.1" && hostHeaders.Length != 1
                || hostHeaders.Any(header => string.IsNullOrWhiteSpace(header.Value)))
            {
                throw new HttpStatusException("400 Bad Request");
            }

            var transferEncodingHeaders = headers
                .Where(header => header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var transferCodings = transferEncodingHeaders
                .SelectMany(header => header.Value.Split(','))
                .Select(value => value.Trim())
                .ToArray();
            if (transferCodings.Any(string.IsNullOrEmpty))
            {
                throw new HttpStatusException("400 Bad Request");
            }
            if (transferCodings.Any(value =>
                !value.Equals("chunked", StringComparison.OrdinalIgnoreCase)))
            {
                throw new HttpStatusException("501 Not Implemented");
            }
            if (transferCodings.Length > 1 || requestLine[2] == "HTTP/1.0" && transferCodings.Length > 0)
            {
                throw new HttpStatusException("400 Bad Request");
            }

            var contentLengthHeaders = headers
                .Where(header => header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (contentLengthHeaders.Length > 1 || transferCodings.Length > 0 && contentLengthHeaders.Length > 0)
            {
                throw new HttpStatusException("400 Bad Request");
            }
            var contentLengthText = contentLengthHeaders.FirstOrDefault().Value;
            if (!string.IsNullOrEmpty(contentLengthText)
                && !int.TryParse(
                    contentLengthText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _
                ))
            {
                throw new HttpStatusException("400 Bad Request");
            }
            var contentLength = string.IsNullOrEmpty(contentLengthText)
                ? 0
                : int.Parse(contentLengthText, NumberStyles.None, CultureInfo.InvariantCulture);
            if (contentLength < 0 || contentLength > MaximumBodySize)
            {
                throw new HttpStatusException("413 Payload Too Large");
            }

            Consume(headerEnd + 4);
            byte[] body;
            timeout.CancelAfter(RequestBodyIdleTimeout);
            bodyProgressTimeout = timeout;
            try
            {
                body = transferCodings.Length == 1
                    ? await ReadChunkedBodyAsync(this, cancellationToken)
                    : await ReadExactlyAsync(contentLength, cancellationToken);
            }
            finally
            {
                bodyProgressTimeout = null;
            }
            return new HttpRequestData(
                requestLine[0].ToUpperInvariant(),
                requestLine[1],
                requestLine[2],
                headers,
                body
            );
        }

        public async Task<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (count == 0 && !await ReadMoreAsync(cancellationToken))
            {
                throw new HttpStatusException("400 Bad Request");
            }
            var value = buffered[start];
            Consume(1);
            return value;
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
                        throw new HttpStatusException("400 Bad Request");
                    }
                    return Encoding.ASCII.GetString(line.ToArray());
                }
                line.WriteByte((byte)value);
                if (line.Length > maximumLength) throw new HttpStatusException("400 Bad Request");
            }
        }

        public async Task CopyExactlyAsync(
            Stream destination,
            int count,
            CancellationToken cancellationToken
        )
        {
            while (count > 0)
            {
                if (this.count == 0 && !await ReadMoreAsync(cancellationToken))
                {
                    throw new HttpStatusException("400 Bad Request");
                }
                var available = Math.Min(count, this.count);
                await destination.WriteAsync(
                    buffered.AsMemory(start, available),
                    cancellationToken
                );
                Consume(available);
                count -= available;
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
                if (count > 0)
                {
                    var available = Math.Min(output.Length - offset, count);
                    buffered.AsSpan(start, available).CopyTo(output.AsSpan(offset));
                    Consume(available);
                    offset += available;
                    continue;
                }
                var read = await stream.ReadAsync(output.AsMemory(offset), cancellationToken);
                if (read == 0) throw new HttpStatusException("400 Bad Request");
                bodyProgressTimeout?.CancelAfter(RequestBodyIdleTimeout);
                offset += read;
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
            if (read > 0) bodyProgressTimeout?.CancelAfter(RequestBodyIdleTimeout);
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
            var maximumCapacity = MaximumHeaderSize + 4;
            if (buffered.Length >= maximumCapacity)
            {
                throw new HttpStatusException("431 Request Header Fields Too Large");
            }
            Array.Resize(
                ref buffered,
                Math.Min(maximumCapacity, checked(buffered.Length * 2))
            );
        }

        private void Consume(int length)
        {
            start += length;
            count -= length;
            if (count == 0) start = 0;
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
                throw new HttpStatusException("400 Bad Request");
            }
            if (!path.StartsWith('/')) throw new HttpStatusException("400 Bad Request");
            return new RequestTarget(string.IsNullOrEmpty(path) ? "/" : path, query);
        }

        private static string RawPathAndQuery(string target)
        {
            if (target.StartsWith('/')) return target;
            if (!Uri.TryCreate(target, UriKind.Absolute, out var absolute)
                || absolute.Scheme is not ("http" or "https")
                || target.Contains('#'))
            {
                throw new HttpStatusException("400 Bad Request");
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

    private sealed record CachedPathCheck(long CheckedAt, string? ResolvedPath);

    private sealed record SiteRoute(
        string DocumentRoot,
        int PhpFastCgiPort,
        bool PhpUsesHttpFallback,
        int? DevelopmentServerPort,
        bool Shared = false
    );

    private readonly record struct LocalResponseResult(bool KeepAlive, int StatusCode);

    private readonly record struct ByteRange(long Offset, long Length, bool IsPartial);

    private sealed class SitePerformanceBucket
    {
        private const int MaximumRecentRequests = 50;
        private readonly object sync = new();
        private readonly Queue<SiteRequestMetric> recent = new();
        private long requestCount;
        private long serverErrorCount;
        private long totalDurationTicks;
        private long slowestDurationTicks;
        private int activeRequests;
        private DateTimeOffset? lastRequestAt;

        public void Begin() => Interlocked.Increment(ref activeRequests);

        public void Complete(string method, string target, int statusCode, TimeSpan duration)
        {
            lock (sync)
            {
                requestCount++;
                if (statusCode >= 500) serverErrorCount++;
                totalDurationTicks += duration.Ticks;
                slowestDurationTicks = Math.Max(slowestDurationTicks, duration.Ticks);
                lastRequestAt = DateTimeOffset.UtcNow;
                recent.Enqueue(new SiteRequestMetric(
                    lastRequestAt.Value,
                    method,
                    DisplayTarget(target),
                    statusCode,
                    duration
                ));
                while (recent.Count > MaximumRecentRequests) recent.Dequeue();
            }
            Interlocked.Decrement(ref activeRequests);
        }

        public SitePerformanceSnapshot Snapshot()
        {
            lock (sync)
            {
                return new SitePerformanceSnapshot(
                    requestCount,
                    serverErrorCount,
                    Volatile.Read(ref activeRequests),
                    requestCount == 0
                        ? TimeSpan.Zero
                        : TimeSpan.FromTicks(totalDurationTicks / requestCount),
                    TimeSpan.FromTicks(slowestDurationTicks),
                    lastRequestAt,
                    recent.Reverse().ToArray()
                );
            }
        }

        private static string DisplayTarget(string target)
        {
            var query = target.IndexOf('?');
            var path = query < 0 ? target : target[..query];
            return path.Length <= 256 ? path : path[..256] + "...";
        }
    }

    private static SitePerformanceSnapshot EmptyPerformance() => new(
        0,
        0,
        0,
        TimeSpan.Zero,
        TimeSpan.Zero,
        null,
        []
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags
    );

    private sealed class HttpStatusException : Exception
    {
        public HttpStatusException(string status) : base(status)
        {
            Status = status;
        }

        public HttpStatusException(string status, Exception innerException)
            : base(status, innerException)
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
