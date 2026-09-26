using System.Net;
using System.Net.Sockets;
using System.Text;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyProxySiteContractsAsync(string supportRoot)
    {
        VerifyProxyTargetParsing();
        VerifyProxyNames();
        VerifyProxyStore(Path.Combine(supportRoot, "proxy-sites"));
        VerifyProxyConfigurationKey();
        await VerifyProxyRouteForwardingAsync();
        await VerifyProxyRouteStreamingAsync();
    }

    private static void VerifyProxyTargetParsing()
    {
        string[] accepted =
        [
            "3000",
            ":3000",
            "localhost:3000",
            "LOCALHOST:3000",
            "127.0.0.1:3000",
            "[::1]:3000",
            "http://localhost:3000",
            "http://127.0.0.1:3000/",
            "  5173  "
        ];
        Check(
            accepted.All(value => ProxySiteStore.TryParseTarget(value, out var port)
                && port is 3000 or 5173),
            "proxy targets accept a port or a loopback host with a port"
        );
        string[] rejected =
        [
            "",
            "   ",
            "80",
            "65536",
            "3000a",
            "-3000",
            "example.com:3000",
            "192.168.1.10:3000",
            "0.0.0.0:3000",
            "https://localhost:3000",
            "ftp://localhost:3000",
            "localhost:3000/app",
            "localhost",
            "http://localhost"
        ];
        Check(
            rejected.All(value => !ProxySiteStore.TryParseTarget(value, out _)),
            "proxy targets reject remote hosts, privileged ports, other schemes, and paths"
        );
    }

    private static void VerifyProxyNames()
    {
        Check(
            ProxySiteStore.NormalizeName(" MyApp ") == "myapp"
                && ProxySiteStore.NormalizeName("my-app2") == "my-app2"
                && ProxySiteStore.NormalizeName(new string('a', 63)) == new string('a', 63),
            "proxy names normalize to lowercase DNS labels"
        );
        string?[] rejected = [null, "", "-app", "app-", "my.app", "my_app", "my app", "caf\u00e9", new string('a', 64)];
        Check(
            rejected.All(value => ProxySiteStore.NormalizeName(value) is null),
            "proxy names reject values that are not a single DNS label"
        );
    }

    private static void VerifyProxyStore(string root)
    {
        var store = new ProxySiteStore(root);
        Check(store.Load().Count == 0, "proxy store starts empty without a settings file");
        store.Save(new ProxySite("Nuxt", 3000), []);
        store.Save(new ProxySite("api", 8000), []);
        store.Save(new ProxySite("nuxt", 3001), []);
        var loaded = store.Load();
        Check(
            loaded.Count == 2
                && loaded[0] == new ProxySite("api", 8000)
                && loaded[1] == new ProxySite("nuxt", 3001),
            "proxy store saves sorted entries and replaces a proxy with the same name"
        );
        Check(
            new ProxySiteStore(root).Load().SequenceEqual(loaded),
            "proxy store persists entries across instances"
        );
        Throws<InvalidOperationException>(
            () => store.Save(new ProxySite("blog", 3002), ["Blog"]),
            "proxy store rejects names that belong to a folder site"
        );
        Throws<ArgumentException>(
            () => store.Save(new ProxySite("bad.name", 3002), []),
            "proxy store rejects invalid names"
        );
        Throws<ArgumentOutOfRangeException>(
            () => store.Save(new ProxySite("low", 80), []),
            "proxy store rejects privileged ports"
        );
        Check(store.Remove("API") && !store.Remove("api"), "proxy store removes entries by name once");
        Check(store.Load().Count == 1, "proxy store keeps other entries after removal");

        File.WriteAllText(store.SettingsPath, "{ not json");
        SettingsFileCache.Invalidate(store.SettingsPath);
        Check(store.Load().Count == 0, "proxy store ignores a corrupt settings file");
        File.WriteAllText(
            store.SettingsPath,
            "{\"SchemaVersion\":2,\"Sites\":[{\"Name\":\"future\",\"Port\":3000}]}"
        );
        SettingsFileCache.Invalidate(store.SettingsPath);
        Check(store.Load().Count == 0, "proxy store ignores an unknown schema version");
        File.WriteAllText(
            store.SettingsPath,
            "{\"SchemaVersion\":1,\"Sites\":[{\"Name\":\"ok\",\"Port\":3000},"
                + "{\"Name\":\"bad name\",\"Port\":3000},{\"Name\":\"low\",\"Port\":22}]}"
        );
        SettingsFileCache.Invalidate(store.SettingsPath);
        Check(
            store.Load().SequenceEqual([new ProxySite("ok", 3000)]),
            "proxy store drops invalid entries from a hand-edited file"
        );
    }

    private static void VerifyProxyConfigurationKey()
    {
        var key = WindowsLocalEnvironment.ProxyConfigurationKey(
            [new ProxySite("a", 3000), new ProxySite("b", 4000)]
        );
        Check(
            key == WindowsLocalEnvironment.ProxyConfigurationKey(
                [new ProxySite("b", 4000), new ProxySite("a", 3000)]
            ),
            "proxy configuration key ignores ordering"
        );
        Check(
            key != WindowsLocalEnvironment.ProxyConfigurationKey(
                [new ProxySite("a", 3001), new ProxySite("b", 4000)]
            ),
            "proxy configuration key changes when a target port changes"
        );
        Check(
            WindowsLocalEnvironment.ProxyConfigurationKey([]) == string.Empty,
            "proxy configuration key is empty without proxies so folder-only keys stay unchanged"
        );
    }

    private static async Task VerifyProxyRouteForwardingAsync()
    {
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var upstreamTask = ServeProxyFixtureAsync(upstream, async (stream, head) =>
        {
            var body = Encoding.ASCII.GetBytes(
                "host=" + FixtureHeader(head, "Host")
                    + ";forwarded=" + FixtureHeader(head, "X-Forwarded-Host")
                    + ";proto=" + FixtureHeader(head, "X-Forwarded-Proto")
            );
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: "
                    + body.Length + "\r\nConnection: close\r\n\r\n"
            ));
            await stream.WriteAsync(body);
        });
        try
        {
            await using var server = new LocalHttpSiteServer();
            var port = await server.StartAsync(
                [new LocalSiteDefinition("app.local-test", string.Empty, DevelopmentServerPort: upstreamPort)],
                phpFastCgiPort: 0,
                preferredPort: ReserveLoopbackPort(),
                fallbackPort: null
            );
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "GET /page?x=1 HTTP/1.1\r\nHost: app.local-test\r\nConnection: close\r\n\r\n"
            ));
            using var received = new MemoryStream();
            await stream.CopyToAsync(received).WaitAsync(TimeSpan.FromSeconds(5));
            var response = Encoding.UTF8.GetString(received.ToArray());
            Check(
                response.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)
                    && response.Contains($"host=127.0.0.1:{upstreamPort}", StringComparison.Ordinal)
                    && response.Contains("forwarded=app.local-test", StringComparison.Ordinal)
                    && response.Contains("proto=http", StringComparison.Ordinal),
                "proxy sites without a folder forward requests to the loopback target with forwarded headers"
            );
            await upstreamTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            upstream.Stop();
        }
    }

    private static async Task VerifyProxyRouteStreamingAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var upstreamTask = ServeProxyFixtureAsync(upstream, async (stream, _) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"
            ));
            await stream.WriteAsync("data: first\n\n"u8.ToArray());
            await stream.FlushAsync();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await stream.WriteAsync("data: second\n\n"u8.ToArray());
        });
        try
        {
            await using var server = new LocalHttpSiteServer();
            var port = await server.StartAsync(
                [new LocalSiteDefinition("events.local-test", string.Empty, DevelopmentServerPort: upstreamPort)],
                phpFastCgiPort: 0,
                preferredPort: ReserveLoopbackPort(),
                fallbackPort: null
            );
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "GET /events HTTP/1.1\r\nHost: events.local-test\r\nConnection: close\r\n\r\n"
            ));
            using var received = new MemoryStream();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await ReadHttpUntilAsync(stream, received, "data: first", timeout.Token);
            }
            var head = Encoding.UTF8.GetString(received.ToArray());
            Check(
                head.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)
                    && head.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase),
                "proxy sites stream event-stream responses before the upstream finishes"
            );
            release.TrySetResult();
            await stream.CopyToAsync(received).WaitAsync(TimeSpan.FromSeconds(5));
            var complete = Encoding.UTF8.GetString(received.ToArray());
            Check(
                complete.Contains("data: second", StringComparison.Ordinal)
                    && complete.EndsWith("0\r\n\r\n", StringComparison.Ordinal),
                "proxy sites finish streamed responses with a terminating chunk"
            );
            await upstreamTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            upstream.Stop();
        }
    }

    private static async Task ServeProxyFixtureAsync(
        TcpListener listener,
        Func<NetworkStream, string, Task> respond
    )
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var head = new MemoryStream();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await ReadHttpUntilAsync(stream, head, "\r\n\r\n", timeout.Token);
        }
        await respond(stream, Encoding.ASCII.GetString(head.ToArray()));
    }

    private static int ReserveLoopbackPort()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        return port;
    }

    private static string FixtureHeader(string head, string name)
    {
        foreach (var line in head.Split("\r\n"))
        {
            var separator = line.IndexOf(':');
            if (separator > 0 && line[..separator].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(separator + 1)..].Trim();
            }
        }
        return string.Empty;
    }
}
