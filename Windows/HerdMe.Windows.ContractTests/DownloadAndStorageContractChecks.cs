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
    internal static async Task VerifyDownloadAndStorageContractsAsync(string supportRoot)
    {
        var coreExecutable = Environment.GetEnvironmentVariable("HERDME_CORE_TEST_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(coreExecutable))
        {
            await VerifyCoreClientAsync(coreExecutable, supportRoot);
        }

        using (var downloadClient = ManagedDownloadClient.Create())
        {
            Check(
                downloadClient.Timeout == TimeSpan.FromMinutes(10),
                "managed downloads use an explicit bounded timeout"
            );
            Check(
                downloadClient.DefaultRequestHeaders.UserAgent.Any(value =>
                    value.Product?.Name == "HerdMe"),
                "managed downloads identify HerdMe to upstream servers"
            );
        }
        var transientDownloadHandler = new SequenceHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("recovered")
            }
        );
        using (var retryingClient = ManagedDownloadClient.Create(
            transientDownloadHandler,
            maximumAttempts: 3,
            delayFactory: _ => TimeSpan.Zero
        ))
        {
            Check(
                await retryingClient.GetStringAsync("https://downloads.example.test/runtime")
                    == "recovered"
                    && transientDownloadHandler.CallCount == 3,
                "managed downloads retry 429 and 5xx responses before succeeding"
            );
        }

        var packageBytes = Encoding.UTF8.GetBytes("verified service package");
        var packageChecksum = Convert.ToHexString(SHA256.HashData(packageBytes));
        var interruptedPackageHandler = new SequenceHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("incomplete")
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(packageBytes)
            }
        );
        using (var packageClient = new HttpClient(interruptedPackageHandler))
        {
            var packagePath = Path.Combine(supportRoot, "retried-service-package.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
            await ServicePackageInstaller.DownloadAndVerifyAsync(
                new ServicePackageRelease(
                    "mariadb",
                    "1.0.0",
                    "mariadb.zip",
                    ServicePackageChecksumAlgorithm.Sha256,
                    packageChecksum,
                    new Uri("https://downloads.example.test/mariadb.zip"),
                    true
                ),
                packagePath,
                CancellationToken.None,
                packageClient,
                maximumAttempts: 2,
                delayFactory: _ => TimeSpan.Zero
            );
            Check(
                File.ReadAllBytes(packagePath).SequenceEqual(packageBytes)
                    && interruptedPackageHandler.CallCount == 2,
                "service downloads discard incomplete files and retry checksum verification"
            );
        }
        var networkFailureHandler = new SequenceHttpMessageHandler(
            _ => throw new HttpRequestException("temporary connection failure"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("connected")
            }
        );
        using (var retryingClient = ManagedDownloadClient.Create(
            networkFailureHandler,
            maximumAttempts: 3,
            delayFactory: _ => TimeSpan.Zero
        ))
        {
            Check(
                await retryingClient.GetStringAsync("https://downloads.example.test/network")
                    == "connected"
                    && networkFailureHandler.CallCount == 2,
                "managed downloads retry transient connection failures"
            );
        }
        var permanentFailureHandler = new SequenceHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
        );
        using (var retryingClient = ManagedDownloadClient.Create(
            permanentFailureHandler,
            maximumAttempts: 3,
            delayFactory: _ => TimeSpan.Zero
        ))
        {
            using var response = await retryingClient.GetAsync(
                "https://downloads.example.test/missing"
            );
            Check(
                response.StatusCode == HttpStatusCode.NotFound
                    && permanentFailureHandler.CallCount == 1,
                "managed downloads do not retry permanent HTTP failures"
            );
        }
        var unsafeRequestHandler = new SequenceHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
        );
        using (var retryingClient = ManagedDownloadClient.Create(
            unsafeRequestHandler,
            maximumAttempts: 3,
            delayFactory: _ => TimeSpan.Zero
        ))
        {
            using var response = await retryingClient.PostAsync(
                "https://downloads.example.test/mutate",
                new StringContent("payload")
            );
            Check(
                response.StatusCode == HttpStatusCode.ServiceUnavailable
                    && unsafeRequestHandler.CallCount == 1,
                "managed downloads never retry non-idempotent requests"
            );
        }
        var cancelledDownloadHandler = new SequenceHttpMessageHandler(
            _ => throw new TaskCanceledException("cancelled fixture"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
        );
        using (var retryingClient = ManagedDownloadClient.Create(
            cancelledDownloadHandler,
            maximumAttempts: 3,
            delayFactory: _ => TimeSpan.Zero
        ))
        {
            await ThrowsAsync<TaskCanceledException>(
                () => retryingClient.GetAsync("https://downloads.example.test/cancelled"),
                "managed downloads propagate cancellation without retrying"
            );
            Check(
                cancelledDownloadHandler.CallCount == 1,
                "managed downloads issue no request after cancellation"
            );
        }

        var maintenanceRoot = Path.Combine(supportRoot, "storage-maintenance");
        var boundedLogPath = Path.Combine(maintenanceRoot, "app.log");
        Directory.CreateDirectory(maintenanceRoot);
        File.WriteAllBytes(boundedLogPath, Enumerable.Repeat((byte)'A', 64).ToArray());
        BoundedLog.AppendLine(boundedLogPath, "new entry", maximumBytes: 32, archiveCount: 2);
        Check(File.ReadAllBytes(boundedLogPath + ".1").Length == 64, "oversized Windows logs are rotated");
        Check(File.ReadAllText(boundedLogPath) == "new entry" + Environment.NewLine, "logging continues after rotation");
        Check(
            await DiagnosticLog.WriteFailureAsync(
                "contracts",
                "fixture-failure",
                "The contract fixture failed.",
                "fixture details",
                maintenanceRoot
            ),
            "structured Windows diagnostics can be persisted"
        );
        var diagnosticLine = File.ReadAllLines(
            Path.Combine(maintenanceRoot, "Log", "diagnostics.jsonl")
        ).Single();
        using (var diagnostic = JsonDocument.Parse(diagnosticLine))
        {
            Check(
                diagnostic.RootElement.GetProperty("level").GetString() == "error"
                    && diagnostic.RootElement.GetProperty("area").GetString() == "contracts"
                    && diagnostic.RootElement.GetProperty("event").GetString() == "fixture-failure"
                    && diagnostic.RootElement.GetProperty("exception").GetString() == "fixture details",
                "structured Windows diagnostics preserve searchable fields"
            );
        }
        await DiagnosticLog.WriteFailureAsync(
            "contracts",
            "fixture-failure",
            "The contract fixture failed.",
            "fixture details",
            maintenanceRoot
        );
        Check(
            File.ReadAllLines(Path.Combine(maintenanceRoot, "Log", "diagnostics.jsonl")).Length == 1,
            "identical Windows diagnostic failures are recorded only once"
        );
        var independentDiagnosticRootA = Path.Combine(supportRoot, "diagnostic-root-a");
        var independentDiagnosticRootB = Path.Combine(supportRoot, "diagnostic-root-b");
        await DiagnosticLog.WriteFailureAsync(
            "contracts",
            "independent-root",
            "The same failure belongs to two support roots.",
            "independent fixture",
            independentDiagnosticRootA
        );
        await DiagnosticLog.WriteFailureAsync(
            "contracts",
            "independent-root",
            "The same failure belongs to two support roots.",
            "independent fixture",
            independentDiagnosticRootB
        );
        Check(
            File.ReadAllLines(
                Path.Combine(independentDiagnosticRootA, "Log", "diagnostics.jsonl")
            ).Length == 1
                && File.ReadAllLines(
                    Path.Combine(independentDiagnosticRootB, "Log", "diagnostics.jsonl")
                ).Length == 1,
            "Windows diagnostic deduplication is isolated to each support root"
        );

        var concurrentDiagnosticRoot = Path.Combine(supportRoot, "diagnostic-concurrent");
        var concurrentWrites = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => DiagnosticLog.WriteFailureAsync(
                "contracts",
                "concurrent-failure",
                "Concurrent callers observed one failure.",
                "concurrent fixture",
                concurrentDiagnosticRoot
            ))
        );
        Check(
            concurrentWrites.All(result => result)
                && File.ReadAllLines(
                    Path.Combine(concurrentDiagnosticRoot, "Log", "diagnostics.jsonl")
                ).Length == 1,
            "concurrent identical Windows diagnostics are persisted atomically once"
        );

        var retryDiagnosticRoot = Path.Combine(supportRoot, "diagnostic-write-retry");
        File.WriteAllText(retryDiagnosticRoot, "This file temporarily blocks the support directory.");
        var blockedDiagnosticWrite = await DiagnosticLog.WriteFailureAsync(
            "contracts",
            "write-retry",
            "The diagnostic write should be retried.",
            "write retry fixture",
            retryDiagnosticRoot
        );
        File.Delete(retryDiagnosticRoot);
        var recoveredDiagnosticWrite = await DiagnosticLog.WriteFailureAsync(
            "contracts",
            "write-retry",
            "The diagnostic write should be retried.",
            "write retry fixture",
            retryDiagnosticRoot
        );
        Check(
            !blockedDiagnosticWrite
                && recoveredDiagnosticWrite
                && File.ReadAllLines(
                    Path.Combine(retryDiagnosticRoot, "Log", "diagnostics.jsonl")
                ).Length == 1,
            "a failed Windows diagnostic write is retried after storage recovers"
        );

        var applicationDiagnosticRoot = Path.Combine(supportRoot, "application-diagnostics");
        var managedServiceId = Guid.NewGuid();
        await ApplicationDiagnostics.WriteEnvironmentStartupFailureAsync(
            new InvalidOperationException("startup fixture"),
            applicationDiagnosticRoot
        );
        await ApplicationDiagnostics.WriteEnvironmentRecoveryFailureAsync(
            new IOException("recovery fixture"),
            applicationDiagnosticRoot
        );
        await ApplicationDiagnostics.WriteBackgroundServiceStartupFailureAsync(
            "mail capture",
            new InvalidOperationException("background fixture"),
            applicationDiagnosticRoot
        );
        await ApplicationDiagnostics.WriteManagedServiceStartupFailureAsync(
            managedServiceId,
            "MySQL",
            new InvalidOperationException("service fixture"),
            applicationDiagnosticRoot
        );
        await ApplicationDiagnostics.WriteUnhandledExceptionAsync(
            new InvalidOperationException("unhandled fixture"),
            applicationDiagnosticRoot
        );
        var applicationDiagnostics = File.ReadAllLines(
            Path.Combine(applicationDiagnosticRoot, "Log", "diagnostics.jsonl")
        ).Select(line => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line)
            ?? throw new InvalidDataException("A structured diagnostic entry was empty."))
            .ToDictionary(
                entry => entry["area"].GetString() + "/" + entry["event"].GetString(),
                StringComparer.Ordinal
            );
        Check(
            applicationDiagnostics.Count == 5
                && applicationDiagnostics.Values.All(entry =>
                    entry["level"].GetString() == "error"
                    && entry["timestamp"].GetDateTimeOffset() != default
                    && !string.IsNullOrWhiteSpace(entry["message"].GetString())
                    && !string.IsNullOrWhiteSpace(entry["exception"].GetString())),
            "all Windows application failure categories preserve the common structured fields"
        );
        Check(
            applicationDiagnostics["environment/automatic-start"]["context"]
                .GetProperty("phase").GetString() == "startup"
                && applicationDiagnostics["environment/automatic-recovery"]["context"]
                    .GetProperty("phase").GetString() == "recovery"
                && applicationDiagnostics["background-services/automatic-start"]["context"]
                    .GetProperty("component").GetString() == "mail capture"
                && applicationDiagnostics["managed-services/automatic-start"]["context"]
                    .GetProperty("serviceId").GetString() == managedServiceId.ToString("D")
                && applicationDiagnostics["application/unhandled-exception"]["context"]
                    .GetProperty("exceptionType").GetString()
                    == typeof(InvalidOperationException).FullName,
            "Windows diagnostics preserve searchable context for each failure category"
        );
        Check(
            UnhandledExceptionPolicy.CanRecover(new InvalidOperationException("recoverable"))
                && !UnhandledExceptionPolicy.CanRecover(new OutOfMemoryException())
                && !UnhandledExceptionPolicy.CanRecover(new AccessViolationException()),
            "the Windows unhandled-error boundary continues only after recoverable failures"
        );
        Check(
            UnhandledExceptionPolicy.UserMessage(new InvalidOperationException("Visible failure"))
                == "Visible failure"
                && UnhandledExceptionPolicy.UserMessage(new Exception(string.Empty)).Contains(
                    "HerdMe's logs",
                    StringComparison.Ordinal
                ),
            "the Windows unhandled-error boundary always provides actionable user text"
        );

        var zipPolicyRoot = Path.Combine(supportRoot, "safe-zip");
        Directory.CreateDirectory(zipPolicyRoot);
        var validZip = Path.Combine(zipPolicyRoot, "valid.zip");
        using (var archive = ZipFile.Open(validZip, ZipArchiveMode.Create))
        {
            var directory = archive.CreateEntry("runtime/");
            directory.ExternalAttributes = 0x10;
            var executable = archive.CreateEntry("runtime/tool.exe");
            await using var output = executable.Open();
            await output.WriteAsync(new byte[] { 1, 2, 3, 4 });
        }
        var validExtraction = Path.Combine(zipPolicyRoot, "valid-output");
        await SafeZipExtractor.ExtractAsync(validZip, validExtraction);
        Check(
            File.ReadAllBytes(Path.Combine(validExtraction, "runtime", "tool.exe"))
                .SequenceEqual(new byte[] { 1, 2, 3, 4 }),
            "safe ZIP extraction preserves regular files inside the destination"
        );

        var traversalZip = Path.Combine(zipPolicyRoot, "traversal.zip");
        using (var archive = ZipFile.Open(traversalZip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("../outside.exe");
        }
        await ThrowsAsync<InvalidDataException>(
            () => SafeZipExtractor.ExtractAsync(
                traversalZip,
                Path.Combine(zipPolicyRoot, "traversal-output")
            ),
            "safe ZIP extraction rejects parent traversal"
        );

        foreach (var reservedPath in new[] { "CON", "nul.txt", "runtime/COM1.log" })
        {
            Throws<InvalidDataException>(
                () => SafeZipExtractor.NormalizeEntryPath(reservedPath),
                $"safe ZIP extraction rejects reserved Windows device path {reservedPath}"
            );
        }

        var symbolicLinkZip = Path.Combine(zipPolicyRoot, "symbolic-link.zip");
        using (var archive = ZipFile.Open(symbolicLinkZip, ZipArchiveMode.Create))
        {
            var link = archive.CreateEntry("runtime-link");
            link.ExternalAttributes = unchecked((int)0xA1FF0000);
            await using var output = link.Open();
            await output.WriteAsync(Encoding.UTF8.GetBytes("../outside"));
        }
        await ThrowsAsync<InvalidDataException>(
            () => SafeZipExtractor.ExtractAsync(
                symbolicLinkZip,
                Path.Combine(zipPolicyRoot, "symbolic-link-output")
            ),
            "safe ZIP extraction rejects symbolic links"
        );

        await ThrowsAsync<InvalidDataException>(
            () => SafeZipExtractor.ExtractAsync(
                validZip,
                Path.Combine(zipPolicyRoot, "entry-limit-output"),
                maximumEntries: 1
            ),
            "safe ZIP extraction enforces the entry-count limit"
        );
        await ThrowsAsync<InvalidDataException>(
            () => SafeZipExtractor.ExtractAsync(
                validZip,
                Path.Combine(zipPolicyRoot, "size-limit-output"),
                maximumExpandedBytes: 3
            ),
            "safe ZIP extraction enforces the expanded-size limit"
        );

        var captureDirectory = Path.Combine(maintenanceRoot, "captures");
        Directory.CreateDirectory(captureDirectory);
        var retentionNow = new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero);
        foreach (var (name, age) in new[]
        {
            ("expired", TimeSpan.FromDays(40)),
            ("oldest", TimeSpan.FromDays(3)),
            ("middle", TimeSpan.FromDays(2)),
            ("newest", TimeSpan.FromDays(1))
        })
        {
            var path = Path.Combine(captureDirectory, name + ".json");
            File.WriteAllText(path, "{}");
            File.SetLastWriteTimeUtc(path, (retentionNow - age).UtcDateTime);
        }
        CaptureRetention.Prune(
            captureDirectory,
            itemLimit: 2,
            maximumAge: TimeSpan.FromDays(30),
            now: retentionNow
        );
        Check(
            Directory.EnumerateFiles(captureDirectory, "*.json")
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(["middle.json", "newest.json"]),
            "Windows capture retention removes expired and excess files"
        );
    }
}
