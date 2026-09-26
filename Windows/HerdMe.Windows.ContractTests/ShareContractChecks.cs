using System.Net;
using System.Net.Sockets;
using System.Text;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyShareContractsAsync()
    {
        VerifyQuickTunnelParsing();
        VerifyCloudflaredReleaseSelection();
        await VerifyShareAliasRoutingAsync();
    }

    private static void VerifyQuickTunnelParsing()
    {
        var uri = SiteShareManager.ParsePublicUri(
            "2026-09-26T10:00:00Z INF |  https://Calm-River-Words.trycloudflare.com                  |"
        );
        Check(
            uri is not null
                && uri.AbsoluteUri == "https://calm-river-words.trycloudflare.com/",
            "share parses the quick tunnel address from cloudflared output"
        );
        Check(
            SiteShareManager.ParsePublicUri("INF Requesting new quick Tunnel on trycloudflare.com...") is null
                && SiteShareManager.ParsePublicUri("https://example.com/trycloudflare.com") is null
                && SiteShareManager.ParsePublicUri("http://abc.trycloudflare.com") is null
                && SiteShareManager.ParsePublicUri(null) is null,
            "share ignores cloudflared lines without an https quick tunnel address"
        );
        var arguments = SiteShareManager.TunnelArguments(80);
        Check(
            arguments.Contains("--no-autoupdate")
                && arguments.Contains("http://127.0.0.1:80")
                && !arguments.Any(argument => argument.Contains("0.0.0.0", StringComparison.Ordinal)),
            "share tunnels connect only to the loopback HTTP listener and never self-update"
        );
    }

    private static void VerifyCloudflaredReleaseSelection()
    {
        var digest = new string('a', 64);
        string Metadata(string assetDigest, string url, long size = 1_000) =>
            "{\"tag_name\":\"2026.9.0\",\"assets\":["
                + "{\"name\":\"cloudflared-linux-amd64\",\"digest\":\"sha256:" + digest + "\","
                + "\"size\":10,\"browser_download_url\":\"https://github.com/cloudflare/cloudflared/releases/download/2026.9.0/cloudflared-linux-amd64\"},"
                + "{\"name\":\"cloudflared-windows-amd64.exe\",\"digest\":\"" + assetDigest + "\","
                + "\"size\":" + size + ",\"browser_download_url\":\"" + url + "\"}]}";
        const string official =
            "https://github.com/cloudflare/cloudflared/releases/download/2026.9.0/cloudflared-windows-amd64.exe";
        var release = CloudflaredInstaller.SelectRelease(Metadata("sha256:" + digest, official));
        Check(
            release.Version == "2026.9.0"
                && release.Sha256 == digest
                && release.Size == 1_000
                && release.DownloadUri.AbsoluteUri == official,
            "cloudflared release selection uses the official Windows x64 asset and its SHA-256 digest"
        );
        Throws<InvalidDataException>(
            () => CloudflaredInstaller.SelectRelease(Metadata("", official)),
            "cloudflared release selection requires a published SHA-256 digest"
        );
        Throws<InvalidDataException>(
            () => CloudflaredInstaller.SelectRelease(Metadata(
                "sha256:" + digest,
                "https://example.com/cloudflare/cloudflared/releases/download/2026.9.0/cloudflared-windows-amd64.exe"
            )),
            "cloudflared release selection rejects downloads outside Cloudflare's GitHub releases"
        );
        Throws<InvalidDataException>(
            () => CloudflaredInstaller.SelectRelease(Metadata(
                "sha256:" + digest,
                "https://github.com/someone/cloudflared/releases/download/2026.9.0/cloudflared-windows-amd64.exe"
            )),
            "cloudflared release selection rejects forks"
        );
        Throws<InvalidDataException>(
            () => CloudflaredInstaller.SelectRelease(
                "{\"tag_name\":\"../../evil\",\"assets\":[]}"
            ),
            "cloudflared release selection rejects unsafe version names"
        );
    }

    private static async Task VerifyShareAliasRoutingAsync()
    {
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var upstreamTask = ServeProxyFixtureAsync(upstream, async (stream, head) =>
        {
            var body = Encoding.ASCII.GetBytes(
                "forwarded=" + FixtureHeader(head, "X-Forwarded-Host")
                    + ";proto=" + FixtureHeader(head, "X-Forwarded-Proto")
            );
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n"
            ));
            await stream.WriteAsync(body);
        });
        try
        {
            await using var server = new LocalHttpSiteServer();
            var port = await server.StartAsync(
                [new LocalSiteDefinition("shared.local-test", string.Empty, DevelopmentServerPort: upstreamPort)],
                phpFastCgiPort: 0,
                preferredPort: ReserveLoopbackPort(),
                fallbackPort: null
            );
            Check(
                await FixtureStatusAsync(port, "abc.trycloudflare.com") == "HTTP/1.1 404 Not Found",
                "share hosts are not routed before the user starts sharing"
            );
            server.SetShareAlias("ABC.trycloudflare.com", "shared.local-test");
            Throws<ArgumentException>(
                () => server.SetShareAlias("shared.local-test", "shared.local-test"),
                "share aliases cannot replace an existing local site name"
            );
            var response = await FixtureRequestAsync(port, "abc.trycloudflare.com");
            Check(
                response.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)
                    && response.Contains("forwarded=abc.trycloudflare.com", StringComparison.Ordinal)
                    && response.Contains("proto=https", StringComparison.Ordinal),
                "share aliases reach the local site and report the public https scheme"
            );
            await upstreamTask.WaitAsync(TimeSpan.FromSeconds(5));
            server.RemoveShareAlias("abc.trycloudflare.com");
            Check(
                await FixtureStatusAsync(port, "abc.trycloudflare.com") == "HTTP/1.1 404 Not Found",
                "stopping a share removes its public host route"
            );
        }
        finally
        {
            upstream.Stop();
        }
    }

    private static async Task<string> FixtureRequestAsync(int port, string host)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n\r\n"
        ));
        using var received = new MemoryStream();
        await stream.CopyToAsync(received).WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.UTF8.GetString(received.ToArray());
    }

    private static async Task<string> FixtureStatusAsync(int port, string host)
    {
        var response = await FixtureRequestAsync(port, host);
        var end = response.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? response : response[..end];
    }
}
