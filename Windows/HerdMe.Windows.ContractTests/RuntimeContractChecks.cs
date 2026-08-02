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

internal static partial class ContractChecks
{
    internal static async Task VerifyCoreClientAsync(string coreExecutable, string supportRoot)
    {
        Check(File.Exists(coreExecutable), "CoreClient test executable exists");
        var client = new CoreClient(coreExecutable);
        var doctor = await client.DoctorAsync();
        Check(
            doctor.Runtimes.Select(runtime => runtime.Name).SequenceEqual(
                new[] { "php", "composer", "node", "npm", "nginx", "dnsmasq" }
            ),
            "CoreClient deserializes the real doctor process response"
        );
        Check(!string.IsNullOrWhiteSpace(doctor.SupportPath), "CoreClient preserves the support path");

        var root = Path.Combine(supportRoot, "Core Client Sites");
        var laravel = Path.Combine(root, "Laravel App");
        var linked = Path.Combine(supportRoot, "Core Client Linked Node");
        Directory.CreateDirectory(laravel);
        Directory.CreateDirectory(linked);
        File.WriteAllText(Path.Combine(laravel, "artisan"), "#!/usr/bin/env php\n");
        File.WriteAllText(Path.Combine(laravel, ".herdme-php"), "8.4\n");
        File.WriteAllText(Path.Combine(linked, "package.json"), "{}\n");

        var sites = await client.ScanAsync([root], "unit-test", [linked]);
        var laravelSite = sites.Single(site => site.Name == "Laravel App");
        var linkedSite = sites.Single(site => site.Name == "Core Client Linked Node");
        Check(
            laravelSite.Framework == "Laravel"
                && laravelSite.PhpVersion == "8.4"
                && laravelSite.Domain.EndsWith(".unit-test", StringComparison.Ordinal),
            "CoreClient scans and deserializes a real Laravel site"
        );
        Check(
            linkedSite.Framework == "Node.js" && linkedSite.Linked,
            "CoreClient preserves linked-site metadata across the process boundary"
        );

        var contractExecutable = ContractExecutablePath();
        var extensions = await client.ValidatePhpAsync(contractExecutable);
        Check(
            extensions.Compatible && extensions.Missing.Count == 0,
            "CoreClient pipes PHP module output through the real core executable"
        );

        var previousFailureFixture = Environment.GetEnvironmentVariable(
            "HERDME_CORE_CLIENT_FAILURE_FIXTURE"
        );
        try
        {
            Environment.SetEnvironmentVariable("HERDME_CORE_CLIENT_FAILURE_FIXTURE", "1");
            InvalidOperationException? processFailure = null;
            try
            {
                await new CoreClient(contractExecutable).DoctorAsync();
            }
            catch (InvalidOperationException error)
            {
                processFailure = error;
            }
            Check(
                processFailure?.Message.Contains(
                    Path.GetFileName(contractExecutable),
                    StringComparison.Ordinal
                ) == true
                    && processFailure.Message.Contains("exit code 42", StringComparison.Ordinal)
                    && processFailure.Message.Contains("0x0000002A", StringComparison.Ordinal),
                "CoreClient reports the failed executable and numeric exit code"
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "HERDME_CORE_CLIENT_FAILURE_FIXTURE",
                previousFailureFixture
            );
        }
    }

    internal static string ContractExecutablePath()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The contract executable path is unavailable.");
        var appHostName = OperatingSystem.IsWindows()
            ? "HerdMe.Windows.ContractTests.exe"
            : "HerdMe.Windows.ContractTests";
        var appHost = Path.Combine(AppContext.BaseDirectory, appHostName);
        if (File.Exists(appHost))
        {
            if (Path.GetFileNameWithoutExtension(processPath).Equals(
                    "dotnet",
                    StringComparison.OrdinalIgnoreCase
                )
                && Path.GetDirectoryName(processPath) is { Length: > 0 } runtimeRoot)
            {
                Environment.SetEnvironmentVariable("DOTNET_ROOT", runtimeRoot);
            }
            return appHost;
        }

        return processPath;
    }

    internal static void Check(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException("Failed contract: " + contract);
    }

    internal static void Throws<TException>(Action action, string contract) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException("Failed contract: " + contract);
    }

    internal static async Task ThrowsAsync<TException>(Func<Task> action, string contract) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException("Failed contract: " + contract);
    }

    internal static async Task TestMailCaptureAsync(string supportRoot)
    {
        await using var capture = new MailCaptureService(supportRoot);
        var captured = new TaskCompletionSource<CapturedMail>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.MessageCaptured += (_, message) => captured.TrySetResult(message);
        await capture.StartAsync(0);
        Check(capture.Port is > 0, "SMTP capture resolves an automatic loopback port");

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, capture.Port!.Value);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4 * 1_024, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4 * 1_024, leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };

        Check(await reader.ReadLineAsync() == "220 HerdMe SMTP ready", "SMTP capture sends its greeting");
        await writer.WriteLineAsync("EHLO localhost");
        Check((await reader.ReadLineAsync())?.StartsWith("250-HerdMe", StringComparison.Ordinal) == true, "SMTP capture accepts EHLO");
        await reader.ReadLineAsync();
        await reader.ReadLineAsync();
        await writer.WriteLineAsync("MAIL FROM:<sender@example.test>");
        Check((await reader.ReadLineAsync())?.StartsWith("250", StringComparison.Ordinal) == true, "SMTP capture accepts senders");
        await writer.WriteLineAsync("RCPT TO:<recipient@example.test>");
        Check((await reader.ReadLineAsync())?.StartsWith("250", StringComparison.Ordinal) == true, "SMTP capture accepts recipients");
        await writer.WriteLineAsync("DATA");
        Check((await reader.ReadLineAsync())?.StartsWith("354", StringComparison.Ordinal) == true, "SMTP capture accepts message data");
        await writer.WriteLineAsync("From: sender@example.test");
        await writer.WriteLineAsync("To: recipient@example.test");
        await writer.WriteLineAsync("Subject: Protocol message");
        await writer.WriteLineAsync("Content-Type: text/plain; charset=utf-8");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("Hello from SMTP");
        await writer.WriteLineAsync("..dot-stuffed");
        await writer.WriteLineAsync(".");
        Check((await reader.ReadLineAsync())?.StartsWith("250", StringComparison.Ordinal) == true, "SMTP capture persists message data");

        var message = await captured.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(message.Sender == "sender@example.test", "SMTP capture stores the envelope sender");
        Check(message.Recipients.SequenceEqual(["recipient@example.test"]), "SMTP capture stores envelope recipients");
        Check(message.Subject == "Protocol message", "SMTP capture parses message headers");
        Check(message.Raw.Contains("\r\n.dot-stuffed\r\n"), "SMTP capture unescapes dot-stuffed content");
        Check(capture.Load().Count == 1, "SMTP capture reloads persisted messages");
        capture.Delete(message);
        Check(capture.Load().Count == 0, "SMTP capture deletes persisted messages");

        await writer.WriteLineAsync("QUIT");
        Check((await reader.ReadLineAsync())?.StartsWith("221", StringComparison.Ordinal) == true, "SMTP capture closes sessions cleanly");
    }

    internal static async Task TestDumpCaptureAsync(string supportRoot)
    {
        await using var capture = new DumpCaptureService(supportRoot);
        var captured = new TaskCompletionSource<CapturedDump>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.DumpCaptured += (_, dump) => captured.TrySetResult(dump);
        await capture.StartAsync(0);
        Check(capture.Port is > 0, "dump capture resolves an automatic loopback port");

        const string serialized = "a:2:{s:4:\"file\";s:8:\"demo.php\";s:5:\"value\";a:2:{s:2:\"ok\";b:1;s:5:\"count\";i:3;}}";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(serialized));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, capture.Port!.Value);
        await using var stream = client.GetStream();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4 * 1_024, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = true
        };
        await writer.WriteLineAsync(payload);

        var dump = await captured.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(dump.Source == "demo.php", "dump capture extracts PHP source metadata");
        Check(dump.Summary.Contains("ok: true"), "dump capture decodes PHP booleans");
        Check(dump.Summary.Contains("count: 3"), "dump capture decodes nested PHP arrays");
        Check(capture.Load().Single().Payload == payload, "dump capture reloads persisted payloads");
        capture.Clear();
        Check(capture.Load().Count == 0, "dump capture clears persisted payloads");
    }

    internal static async Task TestFastCgiClientAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestBody = Enumerable.Range(0, 150_000).Select(index => (byte)(index % 251)).ToArray();
        var parameters = new Dictionary<string, string>
        {
            ["REQUEST_METHOD"] = "POST",
            ["SCRIPT_FILENAME"] = "C:\\Sites\\demo\\public\\index.php",
            ["LONG_PARAMETER"] = new string('x', 140)
        };

        var serverTask = HandleFastCgiFixtureAsync(listener, parameters, requestBody);
        try
        {
            var result = await new FastCgiClient().PerformAsync(port, parameters, requestBody)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Check(
                Encoding.UTF8.GetString(result.StandardOutput) == "Status: 200 OK\r\nContent-Type: text/plain\r\n\r\nFastCGI response",
                "FastCGI client joins standard output records"
            );
            Check(Encoding.UTF8.GetString(result.StandardError) == "fixture warning", "FastCGI client returns standard error");
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Stop();
        }
    }

    internal static async Task TestLocalHttpSiteServerAsync(string supportRoot)
    {
        var siteRoot = Path.Combine(supportRoot, "http-site");
        var publicRoot = Path.Combine(siteRoot, "public");
        Directory.CreateDirectory(publicRoot);
        await File.WriteAllTextAsync(Path.Combine(publicRoot, "index.html"), "HerdMe static site");
        await File.WriteAllTextAsync(Path.Combine(publicRoot, "first.txt"), "first response");
        await File.WriteAllTextAsync(Path.Combine(publicRoot, "second.txt"), "second response");
        await File.WriteAllTextAsync(Path.Combine(publicRoot, "fixed.php"), "<?php // FastCGI fixture");
        await File.WriteAllTextAsync(Path.Combine(publicRoot, "stream.php"), "<?php // FastCGI fixture");
        var largeCharacters = new char[2 * 1_024 * 1_024];
        for (var index = 0; index < largeCharacters.Length; index++)
        {
            largeCharacters[index] = (char)('A' + index % 26);
        }
        var largeAsset = new string(largeCharacters);
        await File.WriteAllTextAsync(
            Path.Combine(publicRoot, "large.bin"),
            largeAsset,
            new UTF8Encoding(false)
        );
        await File.WriteAllTextAsync(Path.Combine(siteRoot, "private.txt"), "must not be served");
        var externalRoot = Path.Combine(supportRoot, "http-site-external");
        Directory.CreateDirectory(externalRoot);
        await File.WriteAllTextAsync(Path.Combine(externalRoot, "secret.txt"), "external secret must not be served");
        var externalLink = Path.Combine(publicRoot, "external-link");

        var occupiedReservation = new TcpListener(IPAddress.Loopback, 0);
        occupiedReservation.Start();
        var occupiedPort = ((IPEndPoint)occupiedReservation.LocalEndpoint).Port;
        var fallbackReservation = new TcpListener(IPAddress.Loopback, 0);
        fallbackReservation.Start();
        var fallbackPort = ((IPEndPoint)fallbackReservation.LocalEndpoint).Port;
        fallbackReservation.Stop();
        try
        {
            await using var fallbackServer = new LocalHttpSiteServer();
            var selectedFallback = await fallbackServer.StartAsync(
                [new LocalSiteDefinition("fallback.local-test", siteRoot)],
                phpFastCgiPort: 1,
                preferredPort: occupiedPort,
                fallbackPort: fallbackPort
            );
            Check(selectedFallback == fallbackPort, "local HTTP falls back to the configured high port");

            var exactPortRejected = false;
            await using var exactPortServer = new LocalHttpSiteServer();
            try
            {
                await exactPortServer.StartAsync(
                    [new LocalSiteDefinition("exact.local-test", siteRoot)],
                    phpFastCgiPort: 1,
                    preferredPort: occupiedPort,
                    fallbackPort: null
                );
            }
            catch (InvalidOperationException error)
            {
                exactPortRejected = error.Message.Contains(
                    occupiedPort.ToString(),
                    StringComparison.Ordinal
                );
            }
            Check(
                exactPortRejected,
                "portless local-site mode rejects a hidden fallback port"
            );
        }
        finally
        {
            occupiedReservation.Stop();
        }

        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var preferredPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        await using var server = new LocalHttpSiteServer();
        var port = await server.StartAsync(
            [new LocalSiteDefinition("demo.local-test", siteRoot)],
            phpFastCgiPort: 1,
            preferredPort: preferredPort
        );
        Check(server.IsRunning && port > 0, "local HTTP serving starts on a loopback port");

        var get = await SendHttpRequestAsync(
            port,
            "GET / HTTP/1.1\r\nHost: demo.local-test\r\nConnection: close\r\n\r\n"
        );
        Check(get.StartsWith("HTTP/1.1 200 OK\r\n", StringComparison.Ordinal), "local HTTP serves static GET requests");
        Check(get.EndsWith("HerdMe static site", StringComparison.Ordinal), "local HTTP returns static file contents");
        var performance = server.Performance("demo.local-test");
        Check(
            performance.RequestCount == 1
                && performance.ServerErrorCount == 0
                && performance.RecentRequests.Count == 1
                && performance.RecentRequests[0].StatusCode == 200
                && performance.RecentRequests[0].Target == "/",
            "local HTTP records per-site request performance without query values"
        );

        using (var sequentialClient = new TcpClient())
        {
            await sequentialClient.ConnectAsync(IPAddress.Loopback, port);
            await using var sequentialStream = sequentialClient.GetStream();
            var sequentialReader = new HttpTestResponseReader(sequentialStream);
            using var sequentialTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await sequentialStream.WriteAsync(Encoding.ASCII.GetBytes(
                "GET /first.txt HTTP/1.1\r\nHost: demo.local-test\r\n\r\n"
            ));
            var firstResponse = await sequentialReader.ReadAsync(sequentialTimeout.Token);
            Check(
                firstResponse.StatusLine == "HTTP/1.1 200 OK"
                    && firstResponse.Header("Connection") == "keep-alive"
                    && firstResponse.BodyText == "first response",
                "local HTTP keeps an HTTP/1.1 connection alive after a complete static response"
            );

            await sequentialStream.WriteAsync(Encoding.ASCII.GetBytes(
                "GET /second.txt HTTP/1.1\r\nHost: demo.local-test\r\nConnection: close\r\n\r\n"
            ));
            var secondResponse = await sequentialReader.ReadAsync(sequentialTimeout.Token);
            Check(
                secondResponse.StatusLine == "HTTP/1.1 200 OK"
                    && secondResponse.Header("Connection") == "close"
                    && secondResponse.BodyText == "second response",
                "local HTTP serves a second sequential request on the same connection"
            );
            Check(
                await sequentialReader.ReachesEndOfStreamAsync(TimeSpan.FromSeconds(2)),
                "local HTTP closes a sequential connection after the client requests closure"
            );
        }

        using (var pipelinedClient = new TcpClient())
        {
            await pipelinedClient.ConnectAsync(IPAddress.Loopback, port);
            await using var pipelinedStream = pipelinedClient.GetStream();
            var pipelinedReader = new HttpTestResponseReader(pipelinedStream);
            using var pipelinedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipelinedStream.WriteAsync(Encoding.ASCII.GetBytes(
                "GET /first.txt HTTP/1.1\r\nHost: demo.local-test\r\n\r\n"
                    + "GET /second.txt HTTP/1.1\r\nHost: demo.local-test\r\nConnection: close\r\n\r\n"
            ));
            var firstPipelined = await pipelinedReader.ReadAsync(pipelinedTimeout.Token);
            var secondPipelined = await pipelinedReader.ReadAsync(pipelinedTimeout.Token);
            Check(
                firstPipelined.Header("Connection") == "keep-alive"
                    && firstPipelined.BodyText == "first response"
                    && secondPipelined.Header("Connection") == "close"
                    && secondPipelined.BodyText == "second response",
                "local HTTP preserves complete pipelined requests and response boundaries"
            );
            Check(
                await pipelinedReader.ReachesEndOfStreamAsync(TimeSpan.FromSeconds(2)),
                "local HTTP closes a pipelined connection after its terminal response"
            );
        }

        var absoluteGet = await SendHttpRequestAsync(
            port,
            "GET http://demo.local-test/ HTTP/1.1\r\nHost: demo.local-test\r\nConnection: close\r\n\r\n"
        );
        Check(absoluteGet.StartsWith("HTTP/1.1 200 OK\r\n", StringComparison.Ordinal), "local HTTP accepts valid absolute proxy targets");

        var head = await SendHttpRequestAsync(
            port,
            "HEAD / HTTP/1.1\r\nHost: demo.local-test\r\nConnection: close\r\n\r\n"
        );
        Check(head.StartsWith("HTTP/1.1 200 OK\r\n", StringComparison.Ordinal), "local HTTP serves HEAD requests");
        Check(!head.EndsWith("HerdMe static site", StringComparison.Ordinal), "local HTTP omits bodies for HEAD requests");

        var largeGet = await SendHttpRequestAsync(
            port,
            "GET /large.bin HTTP/1.1\r\nHost: demo.local-test\r\nConnection: close\r\n\r\n"
        );
        var largeBodyOffset = largeGet.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        Check(largeBodyOffset >= 4, "large static response contains complete HTTP headers");
        Check(largeGet.StartsWith("HTTP/1.1 200 OK\r\n", StringComparison.Ordinal), "large static GET succeeds");
        Check(largeGet.Contains($"Content-Length: {largeAsset.Length}\r\n", StringComparison.Ordinal), "large static GET reports its full length");
        Check(largeGet.Contains("Accept-Ranges: bytes\r\n", StringComparison.Ordinal), "static responses advertise byte ranges");
        Check(largeGet[largeBodyOffset..] == largeAsset, "large static files stream without truncation");

        const int rangeStart = 1_048_570;
        const int rangeEnd = 1_048_633;
        var partial = await SendHttpRequestAsync(
            port,
            $"GET /large.bin HTTP/1.1\r\nHost: demo.local-test\r\nRange: bytes={rangeStart}-{rangeEnd}\r\nConnection: close\r\n\r\n"
        );
        var partialBodyOffset = partial.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        Check(partial.StartsWith("HTTP/1.1 206 Partial Content\r\n", StringComparison.Ordinal), "static byte ranges return 206");
        Check(
            partial.Contains($"Content-Range: bytes {rangeStart}-{rangeEnd}/{largeAsset.Length}\r\n", StringComparison.Ordinal),
            "static byte ranges report the selected interval"
        );
        Check(
            partial[partialBodyOffset..] == largeAsset[rangeStart..(rangeEnd + 1)],
            "static byte ranges return only the selected bytes"
        );

        var openStart = largeAsset.Length - 37;
        var openEnded = await SendHttpRequestAsync(
            port,
            $"GET /large.bin HTTP/1.1\r\nHost: demo.local-test\r\nRange: bytes={openStart}-\r\nConnection: close\r\n\r\n"
        );
        var openBodyOffset = openEnded.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        Check(openEnded[openBodyOffset..] == largeAsset[^37..], "open-ended static ranges reach the end of the file");

        var suffix = await SendHttpRequestAsync(
            port,
            "GET /large.bin HTTP/1.1\r\nHost: demo.local-test\r\nRange: bytes=-64\r\nConnection: close\r\n\r\n"
        );
        var suffixBodyOffset = suffix.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        Check(suffix[suffixBodyOffset..] == largeAsset[^64..], "suffix static ranges return the requested tail");

        var rangeHead = await SendHttpRequestAsync(
            port,
            "HEAD /large.bin HTTP/1.1\r\nHost: demo.local-test\r\nRange: bytes=10-19\r\nConnection: close\r\n\r\n"
        );
        Check(rangeHead.StartsWith("HTTP/1.1 206 Partial Content\r\n", StringComparison.Ordinal), "HEAD honors static byte ranges");
        Check(rangeHead.Contains("Content-Length: 10\r\n", StringComparison.Ordinal), "ranged HEAD reports the selected length");
        Check(rangeHead.EndsWith("\r\n\r\n", StringComparison.Ordinal), "ranged HEAD omits the response body");

        var unsatisfiable = await SendHttpRequestAsync(
            port,
            $"GET /large.bin HTTP/1.1\r\nHost: demo.local-test\r\nRange: bytes={largeAsset.Length}-\r\nConnection: close\r\n\r\n"
        );
        Check(unsatisfiable.StartsWith("HTTP/1.1 416 Range Not Satisfiable\r\n", StringComparison.Ordinal), "out-of-bounds static ranges return 416");
        Check(
            unsatisfiable.Contains($"Content-Range: bytes */{largeAsset.Length}\r\n", StringComparison.Ordinal),
            "out-of-bounds static ranges report the file length"
        );
        Check(unsatisfiable.EndsWith("\r\n\r\n", StringComparison.Ordinal), "416 static responses do not send a body");

        var traversal = await SendHttpRequestAsync(
            port,
            "GET /%2e%2e/private.txt HTTP/1.1\r\nHost: demo.local-test\r\nConnection: close\r\n\r\n"
        );
        Check(traversal.StartsWith("HTTP/1.1 403 Forbidden\r\n", StringComparison.Ordinal), "local HTTP blocks path traversal");
        Check(!traversal.Contains("must not be served", StringComparison.Ordinal), "local HTTP never leaks files outside the document root");
        var absoluteTraversal = await SendHttpRequestAsync(
            port,
            "GET http://demo.local-test/%2e%2e/private.txt HTTP/1.1\r\nHost: demo.local-test\r\nConnection: close\r\n\r\n"
        );
        Check(absoluteTraversal.StartsWith("HTTP/1.1 403 Forbidden\r\n", StringComparison.Ordinal), "local HTTP blocks traversal in absolute proxy targets");

        string reparseEscape;
        try
        {
            try
            {
                Directory.CreateSymbolicLink(externalLink, externalRoot);
            }
            catch (Exception error) when (
                OperatingSystem.IsWindows()
                && error is IOException or UnauthorizedAccessException or PlatformNotSupportedException
            )
            {
                using var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList =
                    {
                        "/d",
                        "/s",
                        "/c",
                        "mklink",
                        "/J",
                        externalLink,
                        externalRoot
                    }
                }) ?? throw new InvalidOperationException("Windows could not start mklink for the junction contract.");
                var junctionOutput = await junction.StandardOutput.ReadToEndAsync();
                var junctionError = await junction.StandardError.ReadToEndAsync();
                await junction.WaitForExitAsync();
                if (junction.ExitCode != 0 || !Directory.Exists(externalLink))
                {
                    throw new InvalidOperationException(
                        $"Windows could not create the junction contract (exit {junction.ExitCode}): "
                        + junctionOutput + junctionError
                    );
                }
            }

            reparseEscape = await SendHttpRequestAsync(
                port,
                "GET /external-link/secret.txt HTTP/1.1\r\nHost: demo.local-test\r\nConnection: close\r\n\r\n"
            );
        }
        finally
        {
            if (Directory.Exists(externalLink)) Directory.Delete(externalLink);
        }
        Check(
            reparseEscape.StartsWith("HTTP/1.1 403 Forbidden\r\n", StringComparison.Ordinal),
            "local HTTP blocks symlink and junction escapes outside the document root"
        );
        Check(
            !reparseEscape.Contains("external secret must not be served", StringComparison.Ordinal),
            "local HTTP never leaks files through symlinks or junctions"
        );

        var unknownHost = await SendHttpRequestAsync(
            port,
            "GET / HTTP/1.1\r\nHost: unknown.local-test\r\nConnection: close\r\n\r\n"
        );
        Check(unknownHost.StartsWith("HTTP/1.1 404 Not Found\r\n", StringComparison.Ordinal), "local HTTP isolates sites by host name");

        var staticPost = await SendHttpRequestAsync(
            port,
            "POST / HTTP/1.1\r\nHost: demo.local-test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
        );
        Check(staticPost.StartsWith("HTTP/1.1 405 Method Not Allowed\r\n", StringComparison.Ordinal), "local HTTP rejects writes to static files");

        var missingHost = await SendHttpRequestAsync(
            port,
            "GET /first.txt HTTP/1.1\r\nConnection: close\r\n\r\n"
        );
        Check(
            missingHost.StartsWith("HTTP/1.1 400 Bad Request\r\n", StringComparison.Ordinal),
            "local HTTP requires exactly one Host header for HTTP/1.1"
        );
        var duplicateContentLength = await SendHttpRequestAsync(
            port,
            "POST /fixed.php HTTP/1.1\r\nHost: demo.local-test\r\n"
                + "Content-Length: 0\r\nContent-Length: 4\r\n\r\n"
                + "GET /second.txt HTTP/1.1\r\nHost: demo.local-test\r\nConnection: close\r\n\r\n"
        );
        Check(
            duplicateContentLength.StartsWith("HTTP/1.1 400 Bad Request\r\n", StringComparison.Ordinal)
                && !duplicateContentLength.Contains("second response", StringComparison.Ordinal)
                && duplicateContentLength.Split("HTTP/1.1 ", StringSplitOptions.None).Length == 2,
            "local HTTP rejects duplicate Content-Length without processing a smuggled request"
        );
        var ambiguousFraming = await SendHttpRequestAsync(
            port,
            "POST /fixed.php HTTP/1.1\r\nHost: demo.local-test\r\n"
                + "Content-Length: 4\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
        );
        Check(
            ambiguousFraming.StartsWith("HTTP/1.1 400 Bad Request\r\n", StringComparison.Ordinal),
            "local HTTP rejects Content-Length combined with Transfer-Encoding"
        );
        var unsupportedTransferCoding = await SendHttpRequestAsync(
            port,
            "POST /fixed.php HTTP/1.1\r\nHost: demo.local-test\r\n"
                + "Transfer-Encoding: identity\r\nConnection: close\r\n\r\n"
        );
        Check(
            unsupportedTransferCoding.StartsWith("HTTP/1.1 501 Not Implemented\r\n", StringComparison.Ordinal),
            "local HTTP rejects unsupported transfer codings"
        );
        var unsupportedProtocol = await SendHttpRequestAsync(
            port,
            "GET /first.txt HTTP/2.0\r\nHost: demo.local-test\r\nConnection: close\r\n\r\n"
        );
        Check(
            unsupportedProtocol.StartsWith("HTTP/1.1 505 HTTP Version Not Supported\r\n", StringComparison.Ordinal),
            "local HTTP rejects unsupported protocol versions"
        );

        var fixedFastCgiListener = new TcpListener(IPAddress.Loopback, 0);
        fixedFastCgiListener.Start(1);
        var fixedFastCgiPort = ((IPEndPoint)fixedFastCgiListener.LocalEndpoint).Port;
        var fixedFastCgiFixture = HandleFixedLengthFastCgiFixtureAsync(
            fixedFastCgiListener,
            Encoding.ASCII.GetBytes("hello")
        );
        try
        {
            var fixedHttpReservation = new TcpListener(IPAddress.Loopback, 0);
            fixedHttpReservation.Start();
            var fixedHttpPort = ((IPEndPoint)fixedHttpReservation.LocalEndpoint).Port;
            fixedHttpReservation.Stop();
            await using var fixedServer = new LocalHttpSiteServer();
            await fixedServer.StartAsync(
                [new LocalSiteDefinition("fixed.local-test", siteRoot)],
                phpFastCgiPort: fixedFastCgiPort,
                preferredPort: fixedHttpPort
            );

            using var fixedClient = new TcpClient();
            await fixedClient.ConnectAsync(IPAddress.Loopback, fixedHttpPort);
            await using var fixedResponseStream = fixedClient.GetStream();
            var fixedReader = new HttpTestResponseReader(fixedResponseStream);
            using var fixedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await fixedResponseStream.WriteAsync(Encoding.ASCII.GetBytes(
                "POST /fixed.php HTTP/1.1\r\nHost: fixed.local-test\r\n"
                    + "Transfer-Encoding: chunked\r\n\r\n"
                    + "5\r\nhello\r\n0\r\n\r\n"
                    + "GET /first.txt HTTP/1.1\r\nHost: fixed.local-test\r\nConnection: close\r\n\r\n"
            ));
            var fixedResponse = await fixedReader.ReadAsync(fixedTimeout.Token);
            Check(
                fixedResponse.StatusLine == "HTTP/1.1 200 OK"
                    && fixedResponse.Header("Connection") == "keep-alive"
                    && fixedResponse.BodyText == "fixed FastCGI response",
                "local HTTP keeps fixed-length FastCGI responses alive"
            );

            var afterFastCgi = await fixedReader.ReadAsync(fixedTimeout.Token);
            Check(
                afterFastCgi.Header("Connection") == "close"
                    && afterFastCgi.BodyText == "first response",
                "local HTTP preserves a pipelined request after a chunked FastCGI body"
            );
            Check(
                await fixedReader.ReachesEndOfStreamAsync(TimeSpan.FromSeconds(2)),
                "local HTTP closes the fixed-length FastCGI session after its terminal response"
            );
            await fixedFastCgiFixture.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            fixedFastCgiListener.Stop();
        }

        var streamingFastCgiListener = new TcpListener(IPAddress.Loopback, 0);
        streamingFastCgiListener.Start(1);
        var streamingFastCgiPort = ((IPEndPoint)streamingFastCgiListener.LocalEndpoint).Port;
        var allowFastCgiCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var streamingFixture = HandleStreamingFastCgiFixtureAsync(
            streamingFastCgiListener,
            allowFastCgiCompletion.Task
        );
        try
        {
            var httpReservation = new TcpListener(IPAddress.Loopback, 0);
            httpReservation.Start();
            var streamingHttpPort = ((IPEndPoint)httpReservation.LocalEndpoint).Port;
            httpReservation.Stop();
            await using var streamingServer = new LocalHttpSiteServer();
            await streamingServer.StartAsync(
                [new LocalSiteDefinition("stream.local-test", siteRoot)],
                phpFastCgiPort: streamingFastCgiPort,
                preferredPort: streamingHttpPort
            );

            using var streamingClient = new TcpClient();
            await streamingClient.ConnectAsync(IPAddress.Loopback, streamingHttpPort);
            await using var streamingResponse = streamingClient.GetStream();
            await streamingResponse.WriteAsync(Encoding.ASCII.GetBytes(
                "GET /stream.php HTTP/1.1\r\nHost: stream.local-test\r\n\r\n"
            ));
            using var received = new MemoryStream();
            using (var firstChunkTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                await ReadHttpUntilAsync(
                    streamingResponse,
                    received,
                    "\r\n\r\nfirst-",
                    firstChunkTimeout.Token
                );
            }
            var firstResponse = Encoding.UTF8.GetString(received.ToArray());
            var responseHeaderEnd = firstResponse.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Check(
                firstResponse.StartsWith("HTTP/1.1 200 OK\r\n", StringComparison.Ordinal)
                    && firstResponse.EndsWith("first-", StringComparison.Ordinal),
                "local HTTP streams FastCGI output before END_REQUEST"
            );
            var responseHeader = firstResponse[..responseHeaderEnd];
            Check(
                !responseHeader.Contains("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase),
                "local HTTP strips FastCGI hop-by-hop response headers"
            );
            Check(
                !responseHeader.Contains("Content-Length:", StringComparison.OrdinalIgnoreCase),
                "local HTTP does not invent Content-Length for streaming FastCGI responses"
            );
            Check(
                responseHeader.Contains("Connection: close", StringComparison.OrdinalIgnoreCase),
                "local HTTP closes streaming FastCGI responses without a framing length"
            );

            allowFastCgiCompletion.TrySetResult(true);
            await streamingResponse.CopyToAsync(received).WaitAsync(TimeSpan.FromSeconds(3));
            var completeResponse = Encoding.UTF8.GetString(received.ToArray());
            Check(
                completeResponse[(responseHeaderEnd + 4)..] == "first-second",
                "local HTTP preserves every streamed FastCGI body record"
            );
            await streamingFixture.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            allowFastCgiCompletion.TrySetResult(true);
            streamingFastCgiListener.Stop();
        }

        var movedLaravelRoot = Path.Combine(supportRoot, "moved-laravel-site");
        var movedLaravelPublic = Path.Combine(movedLaravelRoot, "public");
        Directory.CreateDirectory(movedLaravelPublic);
        var movedLaravelIndex = Path.Combine(movedLaravelPublic, "index.php");
        await File.WriteAllTextAsync(movedLaravelIndex, "<?php // Laravel front controller");
        await File.WriteAllTextAsync(
            Path.Combine(movedLaravelPublic, "index.html"),
            "legacy hosting placeholder"
        );
        var movedLaravelFastCgiListener = new TcpListener(IPAddress.Loopback, 0);
        movedLaravelFastCgiListener.Start(1);
        var movedLaravelFastCgiPort = ((IPEndPoint)movedLaravelFastCgiListener.LocalEndpoint).Port;
        var movedLaravelFixture = HandleLaravelIndexFastCgiFixtureAsync(
            movedLaravelFastCgiListener,
            movedLaravelIndex
        );
        try
        {
            var movedLaravelHttpReservation = new TcpListener(IPAddress.Loopback, 0);
            movedLaravelHttpReservation.Start();
            var movedLaravelHttpPort = ((IPEndPoint)movedLaravelHttpReservation.LocalEndpoint).Port;
            movedLaravelHttpReservation.Stop();
            await using var movedLaravelServer = new LocalHttpSiteServer();
            await movedLaravelServer.StartAsync(
                [new LocalSiteDefinition("moved-laravel.local-test", movedLaravelRoot)],
                phpFastCgiPort: movedLaravelFastCgiPort,
                preferredPort: movedLaravelHttpPort
            );

            var movedLaravelResponse = await SendHttpRequestAsync(
                movedLaravelHttpPort,
                "GET / HTTP/1.1\r\nHost: moved-laravel.local-test\r\nConnection: close\r\n\r\n"
            );
            Check(
                movedLaravelResponse.EndsWith("Laravel front controller", StringComparison.Ordinal)
                    && !movedLaravelResponse.Contains("legacy hosting placeholder", StringComparison.Ordinal),
                "moved Laravel sites prefer public/index.php over stale hosting placeholders"
            );
            await movedLaravelFixture.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            movedLaravelFastCgiListener.Stop();
        }
    }

    internal static async Task TestCancelledCommandKillsTreeAsync(string supportRoot)
    {
        var marker = Path.Combine(supportRoot, "cancelled-command-finished");
        string executable;
        string[] arguments;
        if (OperatingSystem.IsWindows())
        {
            executable = "cmd.exe";
            arguments = [
                "/d",
                "/s",
                "/c",
                $"ping 127.0.0.1 -n 3 >nul & echo completed>\"{marker}\""
            ];
        }
        else
        {
            executable = "/bin/sh";
            arguments = ["-c", $"sleep 2; touch '{marker.Replace("'", "'\\''")}'"];
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var wasCancelled = false;
        try
        {
            await ComposerToolManager.RunAsync(
                executable,
                arguments,
                supportRoot,
                new Dictionary<string, string>(),
                cancellation.Token
            );
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            wasCancelled = true;
        }
        await Task.Delay(TimeSpan.FromSeconds(2.3));
        Check(wasCancelled, "managed commands propagate cancellation");
        Check(!File.Exists(marker), "managed command cancellation terminates the process tree");
    }

    internal static async Task<string> SendHttpRequestAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        client.Client.Shutdown(SocketShutdown.Send);
        using var response = new MemoryStream();
        await stream.CopyToAsync(response);
        return Encoding.UTF8.GetString(response.ToArray());
    }

    internal static async Task ReadHttpUntilAsync(
        Stream source,
        MemoryStream destination,
        string expected,
        CancellationToken cancellationToken
    )
    {
        var buffer = new byte[4 * 1_024];
        while (!Encoding.UTF8.GetString(destination.ToArray()).Contains(expected, StringComparison.Ordinal))
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0) throw new EndOfStreamException("The HTTP streaming fixture closed early.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    internal static async Task VerifyLiveServiceReleasesAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        probe.DefaultRequestHeaders.UserAgent.ParseAdd("HerdMe/1.0 (+https://github.com/Hamad3bdulla/herdme)");
        var installer = new ServicePackageInstaller();

        foreach (var definition in ManagedServiceCatalog.All.Where(definition => definition.IsInstallable))
        {
            var release = await installer.ResolveReleaseAsync(definition.Id, timeout.Token);
            Check(release.DefinitionId == definition.Id, $"{definition.Name} release metadata preserves its service identifier");
            Check(!string.IsNullOrWhiteSpace(release.Version), $"{definition.Name} release metadata includes a version");
            Check(!string.IsNullOrWhiteSpace(release.FileName), $"{definition.Name} release metadata includes a filename");
            Check(release.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, $"{definition.Name} release filename is safe");
            Check(release.DownloadUri.Scheme == Uri.UriSchemeHttps, $"{definition.Name} release uses HTTPS");
            var checksumLength = release.ChecksumAlgorithm == ServicePackageChecksumAlgorithm.Sha256 ? 64 : 32;
            Check(
                release.Checksum.Length == checksumLength && release.Checksum.All(Uri.IsHexDigit),
                $"{definition.Name} release includes a valid {release.ChecksumAlgorithm} checksum"
            );

            await ProbeDownloadAsync(probe, release.DownloadUri, definition.Name, timeout.Token);
            Console.WriteLine(
                $"{definition.Id}: {release.Version} [{release.ChecksumAlgorithm}] {release.DownloadUri}"
            );
        }
    }

    internal static async Task VerifyLiveRuntimeReleasesAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        probe.DefaultRequestHeaders.UserAgent.ParseAdd("HerdMe/1.0 (+https://github.com/Hamad3bdulla/herdme)");

        var phpInstaller = new PhpRuntimeInstaller();
        foreach (var cycle in PhpRuntimeInstaller.SupportedCycles)
        {
            var release = await phpInstaller.ResolveReleaseAsync(cycle, timeout.Token);
            Check(release.Cycle == cycle, $"PHP {cycle} release metadata preserves its cycle");
            Check(Version.TryParse(release.Version, out _), $"PHP {cycle} release metadata includes a version");
            Check(release.Sha256.Length == 64 && release.Sha256.All(Uri.IsHexDigit), $"PHP {cycle} includes SHA-256");
            Check(release.DownloadUri.Scheme == Uri.UriSchemeHttps, $"PHP {cycle} uses HTTPS");
            Check(
                release.FileName.Contains("-nts-Win32-vs", StringComparison.OrdinalIgnoreCase)
                    && release.FileName.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase),
                $"PHP {cycle} selects NTS x64"
            );
            await ProbeDownloadAsync(probe, release.DownloadUri, $"PHP {cycle}", timeout.Token);
            Console.WriteLine($"php-{cycle}: {release.Version} [Sha256] {release.DownloadUri}");
        }

        var nodeInstaller = new NodeRuntimeInstaller();
        foreach (var major in RuntimeCatalog.WindowsNodeMajors)
        {
            var release = await nodeInstaller.ResolveReleaseAsync(major, timeout.Token);
            Check(release.Major == major, $"Node.js {major} release metadata preserves its major");
            Check(Version.TryParse(release.Version, out _), $"Node.js {major} release metadata includes a version");
            Check(release.Sha256.Length == 64 && release.Sha256.All(Uri.IsHexDigit), $"Node.js {major} includes SHA-256");
            Check(release.DownloadUri.Scheme == Uri.UriSchemeHttps, $"Node.js {major} uses HTTPS");
            Check(release.ArchiveName.EndsWith("-win-x64.zip", StringComparison.Ordinal), $"Node.js {major} selects x64");
            await ProbeDownloadAsync(probe, release.DownloadUri, $"Node.js {major}", timeout.Token);
            Console.WriteLine($"node-{major}: {release.Version} [Sha256] {release.DownloadUri}");
        }

        var tools = new ComposerToolManager();
        var composer = await tools.ResolveComposerReleaseAsync(timeout.Token);
        Check(Version.TryParse(composer.Version, out _), "Composer release metadata includes a stable version");
        Check(composer.Sha256.Length == 64 && composer.Sha256.All(Uri.IsHexDigit), "Composer includes SHA-256");
        Check(composer.DownloadUri.Scheme == Uri.UriSchemeHttps, "Composer uses HTTPS");
        await ProbeDownloadAsync(probe, composer.DownloadUri, "Composer", timeout.Token);
        Console.WriteLine($"composer: {composer.Version} [Sha256] {composer.DownloadUri}");

        var laravel = await tools.LatestLaravelInstallerVersionAsync(timeout.Token);
        Check(Version.TryParse(laravel, out _), "Laravel Installer metadata includes a stable version");
        Console.WriteLine($"laravel-installer: {laravel}");

        var gitInstaller = new GitRuntimeInstaller();
        var git = await gitInstaller.ResolveReleaseAsync(timeout.Token);
        Check(Version.TryParse(git.Version, out _), "Git release metadata includes a stable version");
        Check(git.FileName.EndsWith("-64-bit.zip", StringComparison.Ordinal), "Git selects MinGit x64");
        Check(git.Size > 0, "Git release metadata includes the archive size");
        Check(git.Sha256.Length == 64 && git.Sha256.All(Uri.IsHexDigit), "Git includes SHA-256");
        Check(git.DownloadUri.Scheme == Uri.UriSchemeHttps, "Git uses HTTPS");
        await ProbeDownloadAsync(probe, git.DownloadUri, "Git for Windows", timeout.Token);
        Console.WriteLine($"git: {git.Version} [Sha256] {git.DownloadUri}");

        var xdebugMetadata = await probe.GetStringAsync(
            "https://api.github.com/repos/xdebug/xdebug/releases/latest",
            timeout.Token
        );
        var xdebugRoot = Path.Combine(Path.GetTempPath(), "herdme-xdebug-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(xdebugRoot);
        try
        {
            foreach (var cycle in PhpRuntimeInstaller.SupportedCycles)
            {
                var release = XdebugManager.SelectWindowsRelease(xdebugMetadata, cycle, "nts", "x86_64");
                Check(Version.TryParse(release.Version, out _), $"Xdebug for PHP {cycle} includes a stable version");
                Check(release.Sha256.Length == 64 && release.Sha256.All(Uri.IsHexDigit), $"Xdebug for PHP {cycle} includes SHA-256");
                var archive = Path.Combine(xdebugRoot, release.FileName);
                var dll = Path.Combine(xdebugRoot, release.DllName);
                await XdebugManager.DownloadAndVerifyAsync(release, archive, timeout.Token);
                await XdebugManager.ExtractDllAsync(release, archive, dll, timeout.Token);
                Check(new FileInfo(dll).Length > 0, $"Xdebug for PHP {cycle} extracts a non-empty DLL");
                Console.WriteLine($"xdebug-php-{cycle}: {release.Version} [Sha256 verified] {release.DownloadUri}");
            }
        }
        finally
        {
            if (Directory.Exists(xdebugRoot)) Directory.Delete(xdebugRoot, true);
        }
    }

    internal static async Task VerifyLiveGitInstallAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The live MinGit install requires Windows.");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var supportRoot = Path.Combine(
            Path.GetTempPath(),
            "herdme-live-git-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(supportRoot);
        try
        {
            var installer = new GitRuntimeInstaller(supportRoot);
            var git = await installer.EnsureInstalledAsync(timeout.Token);
            Check(File.Exists(git), "the live MinGit install produces cmd\\git.exe");

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = string.Join(Path.PathSeparator, [
                    Path.GetDirectoryName(git)!,
                    Environment.SystemDirectory
                ])
            };
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var version = await ComposerToolManager.RunAsync(
                cmd,
                ["/d", "/s", "/c", "git --version"],
                supportRoot,
                environment,
                timeout.Token
            );
            Check(
                version.StartsWith("git version ", StringComparison.OrdinalIgnoreCase),
                "a clean CMD resolves the managed Git command from PATH"
            );

            var repository = Path.Combine(supportRoot, "project");
            Directory.CreateDirectory(repository);
            await ComposerToolManager.RunAsync(
                cmd,
                ["/d", "/s", "/c", "git init --quiet"],
                repository,
                environment,
                timeout.Token
            );
            Check(
                Directory.Exists(Path.Combine(repository, ".git")),
                "managed Git initializes a real repository from a clean CMD"
            );
            Console.WriteLine(version);
        }
        finally
        {
            await LaravelProjectCreator.DeleteStagingDirectoryAsync(supportRoot);
        }
    }

    internal static async Task VerifyLiveManagedCommandPathAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The managed command PATH check requires Windows.");
        }
        var coreExecutable = Environment.GetEnvironmentVariable("HERDME_CORE_TEST_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(coreExecutable) || !File.Exists(coreExecutable))
        {
            throw new InvalidOperationException(
                "Set HERDME_CORE_TEST_EXECUTABLE to the built herdme-core.exe."
            );
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var core = new CoreClient(coreExecutable);
        var vcRuntimeRoot = Path.Combine(
            FindRepositoryRoot(),
            "build",
            "windows-vc143-runtime"
        );
        var php = new PhpRuntimeInstaller(core, vcRuntimeRoot: vcRuntimeRoot);
        var policy = new PhpRuntimePolicy(core);
        var node = new NodeRuntimeInstaller();
        var tools = new ComposerToolManager(
            coreClient: core,
            phpInstaller: php,
            phpPolicy: policy,
            nodeInstaller: node
        );
        var git = new GitRuntimeInstaller(tools.SupportRoot);
        var userPath = new WindowsUserPathManager(tools.SupportRoot);
        var setup = new InitialSetupManager(
            coreClient: core,
            phpInstaller: php,
            phpPolicy: policy,
            composerTools: tools,
            nodeInstaller: node,
            gitInstaller: git,
            userPathManager: userPath
        );
        await setup.EnsureCommandLineToolsAsync(cancellationToken: timeout.Token);

        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var emptyEnvironment = new Dictionary<string, string>();
        foreach (var (command, expected) in new Dictionary<string, string>
        {
            ["php --version"] = "PHP ",
            ["composer --version --no-ansi"] = "Composer version ",
            ["laravel --version --no-ansi"] = "Laravel Installer ",
            ["node --version"] = "v",
            ["npm --version"] = ".",
            ["git --version"] = "git version "
        })
        {
            var output = await ComposerToolManager.RunAsync(
                cmd,
                ["/d", "/s", "/c", command],
                tools.SupportRoot,
                emptyEnvironment,
                timeout.Token
            );
            Check(
                output.Contains(expected, StringComparison.OrdinalIgnoreCase),
                $"a new CMD can execute managed {command.Split(' ')[0]}"
            );
            Console.WriteLine(output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim());
        }

        var supportPrefix = Path.GetFullPath(tools.SupportRoot)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        foreach (var command in new[] { "php", "composer", "laravel", "node", "npm", "git" })
        {
            var resolved = await ComposerToolManager.RunAsync(
                cmd,
                ["/d", "/s", "/c", "where " + command],
                tools.SupportRoot,
                emptyEnvironment,
                timeout.Token
            );
            var first = resolved.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )[0];
            Check(
                Path.GetFullPath(first).StartsWith(supportPrefix, StringComparison.OrdinalIgnoreCase),
                $"CMD resolves {command} from HerdMe before system tools"
            );
        }

        var projectRoot = Path.Combine(
            Path.GetTempPath(),
            "herdme-live-laravel-git-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(projectRoot);
        try
        {
            var creator = new LaravelProjectCreator(
                tools,
                php,
                policy,
                node,
                git,
                userPath
            );
            var project = await creator.CreateAsync(
                new LaravelProjectRequest(
                    "git-smoke",
                    projectRoot,
                    "None",
                    "Pest",
                    InstallBoost: false,
                    InitializeGit: true
                ),
                cancellationToken: timeout.Token
            );
            Check(File.Exists(Path.Combine(project, "artisan")), "managed Laravel creates artisan");
            Check(
                File.Exists(Path.Combine(project, "vendor", "autoload.php")),
                "managed Composer installs Laravel dependencies"
            );
            Check(
                Directory.Exists(Path.Combine(project, ".git")),
                "Laravel project creation initializes a repository with managed Git"
            );
            Console.WriteLine("Laravel managed-Git project creation passed");
        }
        finally
        {
            await LaravelProjectCreator.DeleteStagingDirectoryAsync(projectRoot);
        }
    }

    internal static async Task ProbeDownloadAsync(
        HttpClient probe,
        Uri uri,
        string name,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var response = await probe.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        Check(response.IsSuccessStatusCode, $"{name} release URL is reachable ({(int)response.StatusCode})");
    }

    internal static async Task HandleFastCgiFixtureAsync(
        TcpListener listener,
        IReadOnlyDictionary<string, string> expectedParameters,
        byte[] expectedBody
    )
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var encodedParameters = new MemoryStream();
        using var body = new MemoryStream();
        var inputRecordLengths = new List<int>();
        var sawBeginRequest = false;
        var parametersEnded = false;

        while (true)
        {
            var header = new byte[8];
            await ReadExactlyAsync(stream, header);
            Check(header[0] == 1, "FastCGI request uses protocol version 1");
            Check(BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2)) == 1, "FastCGI request uses a consistent identifier");
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
            var content = new byte[length];
            await ReadExactlyAsync(stream, content);
            if (header[6] > 0) await ReadExactlyAsync(stream, new byte[header[6]]);

            switch (header[1])
            {
                case 1:
                    sawBeginRequest = true;
                    Check(content.Length == 8 && content[1] == 1, "FastCGI request begins with responder role");
                    break;
                case 4 when content.Length == 0:
                    parametersEnded = true;
                    break;
                case 4:
                    encodedParameters.Write(content);
                    break;
                case 5:
                    inputRecordLengths.Add(content.Length);
                    if (content.Length == 0) goto RequestComplete;
                    body.Write(content);
                    break;
            }
        }

    RequestComplete:
        Check(sawBeginRequest, "FastCGI client sends begin-request");
        Check(parametersEnded, "FastCGI client terminates parameter records");
        var decodedParameters = DecodeFastCgiParameters(encodedParameters.ToArray());
        Check(
            expectedParameters.All(item => decodedParameters.GetValueOrDefault(item.Key) == item.Value),
            "FastCGI client encodes short and long parameters"
        );
        Check(body.ToArray().SequenceEqual(expectedBody), "FastCGI client preserves request bodies");
        Check(
            inputRecordLengths.SequenceEqual([65_535, 65_535, 18_930, 0]),
            "FastCGI client splits bodies larger than 65535 bytes"
        );

        await WriteFastCgiRecordAsync(stream, 6, Encoding.UTF8.GetBytes("Status: 200 OK\r\nContent-Type: text/plain\r\n\r\n"));
        await WriteFastCgiRecordAsync(stream, 7, Encoding.UTF8.GetBytes("fixture warning"));
        await WriteFastCgiRecordAsync(stream, 6, Encoding.UTF8.GetBytes("FastCGI response"));
        await WriteFastCgiRecordAsync(stream, 3, new byte[8]);
    }

    internal static async Task HandleStreamingFastCgiFixtureAsync(
        TcpListener listener,
        Task allowCompletion
    )
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        while (true)
        {
            var header = new byte[8];
            await ReadExactlyAsync(stream, header);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
            if (length > 0) await ReadExactlyAsync(stream, new byte[length]);
            if (header[6] > 0) await ReadExactlyAsync(stream, new byte[header[6]]);
            if (header[1] == 5 && length == 0) break;
        }

        await WriteFastCgiRecordAsync(
            stream,
            6,
            Encoding.UTF8.GetBytes("Status: 200 OK\r\nContent-Type: text/plain")
        );
        await WriteFastCgiRecordAsync(
            stream,
            6,
            Encoding.UTF8.GetBytes("\r\nTransfer-Encoding: chunked\r\n\r\nfirst-")
        );
        await allowCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await WriteFastCgiRecordAsync(stream, 6, Encoding.UTF8.GetBytes("second"));
        await WriteFastCgiRecordAsync(stream, 3, new byte[8]);
    }

    internal static async Task HandleFixedLengthFastCgiFixtureAsync(
        TcpListener listener,
        byte[] expectedBody
    )
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var encodedParameters = new MemoryStream();
        using var requestBody = new MemoryStream();
        while (true)
        {
            var header = new byte[8];
            await ReadExactlyAsync(stream, header);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
            var content = new byte[length];
            if (length > 0) await ReadExactlyAsync(stream, content);
            if (header[6] > 0) await ReadExactlyAsync(stream, new byte[header[6]]);
            if (header[1] == 4 && length > 0) encodedParameters.Write(content);
            if (header[1] == 5 && length > 0) requestBody.Write(content);
            if (header[1] == 5 && length == 0) break;
        }
        var parameters = DecodeFastCgiParameters(encodedParameters.ToArray());
        Check(
            parameters.GetValueOrDefault("VAR_DUMPER_FORMAT") == "server"
                && parameters.GetValueOrDefault("VAR_DUMPER_SERVER") == "127.0.0.1:9912",
            "local HTTP routes Symfony VarDumper payloads to HerdMe automatically"
        );
        Check(
            requestBody.ToArray().SequenceEqual(expectedBody),
            "local HTTP decodes chunked request bodies before FastCGI"
        );

        var body = Encoding.UTF8.GetBytes("fixed FastCGI response");
        await WriteFastCgiRecordAsync(
            stream,
            6,
            Encoding.UTF8.GetBytes(
                "Status: 200 OK\r\nContent-Type: text/plain\r\n"
                    + $"Content-Length: {body.Length}\r\n\r\n"
            )
        );
        await WriteFastCgiRecordAsync(stream, 6, body);
        await WriteFastCgiRecordAsync(stream, 3, new byte[8]);
    }

    internal static async Task HandleLaravelIndexFastCgiFixtureAsync(
        TcpListener listener,
        string expectedScript
    )
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var encodedParameters = new MemoryStream();
        while (true)
        {
            var header = new byte[8];
            await ReadExactlyAsync(stream, header);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
            var content = new byte[length];
            if (length > 0) await ReadExactlyAsync(stream, content);
            if (header[6] > 0) await ReadExactlyAsync(stream, new byte[header[6]]);
            if (header[1] == 4 && length > 0) encodedParameters.Write(content);
            if (header[1] == 5 && length == 0) break;
        }
        var parameters = DecodeFastCgiParameters(encodedParameters.ToArray());
        Check(
            string.Equals(
                parameters.GetValueOrDefault("SCRIPT_FILENAME"),
                Path.GetFullPath(expectedScript),
                StringComparison.OrdinalIgnoreCase
            )
                && parameters.GetValueOrDefault("SCRIPT_NAME") == "/index.php",
            "local HTTP dispatches a moved Laravel site to its PHP front controller"
        );

        var body = Encoding.UTF8.GetBytes("Laravel front controller");
        await WriteFastCgiRecordAsync(
            stream,
            6,
            Encoding.UTF8.GetBytes(
                "Status: 200 OK\r\nContent-Type: text/plain\r\n"
                    + $"Content-Length: {body.Length}\r\n\r\n"
            )
        );
        await WriteFastCgiRecordAsync(stream, 6, body);
        await WriteFastCgiRecordAsync(stream, 3, new byte[8]);
    }

    internal static Dictionary<string, string> DecodeFastCgiParameters(byte[] content)
    {
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        var offset = 0;
        while (offset < content.Length)
        {
            var nameLength = ReadFastCgiLength(content, ref offset);
            var valueLength = ReadFastCgiLength(content, ref offset);
            if (offset + nameLength + valueLength > content.Length)
            {
                throw new InvalidDataException("Malformed FastCGI fixture parameters.");
            }
            var name = Encoding.UTF8.GetString(content, offset, nameLength);
            offset += nameLength;
            var value = Encoding.UTF8.GetString(content, offset, valueLength);
            offset += valueLength;
            output[name] = value;
        }
        return output;
    }

    internal static int ReadFastCgiLength(byte[] content, ref int offset)
    {
        if (offset >= content.Length) throw new InvalidDataException("Missing FastCGI fixture length.");
        if ((content[offset] & 0x80) == 0) return content[offset++];
        if (offset + 4 > content.Length) throw new InvalidDataException("Truncated FastCGI fixture length.");
        var length = BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(offset, 4)) & 0x7fff_ffff;
        offset += 4;
        return checked((int)length);
    }

    internal static async Task WriteFastCgiRecordAsync(Stream stream, byte type, byte[] content)
    {
        var padding = (8 - content.Length % 8) % 8;
        var header = new byte[8];
        header[0] = 1;
        header[1] = type;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), checked((ushort)content.Length));
        header[6] = (byte)padding;
        await stream.WriteAsync(header);
        await stream.WriteAsync(content);
        if (padding > 0) await stream.WriteAsync(new byte[padding]);
    }

    internal static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..]);
            if (count == 0) throw new EndOfStreamException("Fixture connection closed early.");
            offset += count;
        }
    }

}
