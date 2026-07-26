using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

if (args.SequenceEqual(["-m"], StringComparer.Ordinal))
{
    Console.WriteLine("[PHP Modules]");
    foreach (var module in new[]
    {
        "ctype", "curl", "dom", "fileinfo", "filter", "hash", "mbstring",
        "openssl", "pcre", "pdo", "session", "tokenizer", "xml"
    })
    {
        Console.WriteLine(module);
    }
    return;
}

var verifyLiveServices = args.Contains("--live-service-releases", StringComparer.Ordinal);
var verifyLiveRuntimes = args.Contains("--live-runtime-releases", StringComparer.Ordinal);
if (verifyLiveServices || verifyLiveRuntimes)
{
    if (verifyLiveServices) await VerifyLiveServiceReleasesAsync();
    if (verifyLiveRuntimes) await VerifyLiveRuntimeReleasesAsync();
    Console.WriteLine("HerdMe Windows live release checks passed");
    return;
}

var repositoryRoot = FindRepositoryRoot();
VerifyReleaseAndInstallerContracts(repositoryRoot);

var supportRoot = Path.Combine(
    Path.GetTempPath(),
    "herdme-windows-contracts-" + Guid.NewGuid().ToString("N")
);
try
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

    var freshStore = new SiteConfigurationStore(Path.Combine(supportRoot, "fresh"));
    var freshSettings = freshStore.Load();
    Check(!freshSettings.OnboardingCompleted, "new installations require initial setup");
    Check(
        freshSettings.SchemaVersion == SiteConfigurationStore.CurrentSchemaVersion,
        "new Windows site settings use the current schema"
    );
    var legacyStore = new SiteConfigurationStore(Path.Combine(supportRoot, "legacy"));
    Directory.CreateDirectory(Path.GetDirectoryName(legacyStore.SettingsPath)!);
    File.WriteAllText(
        legacyStore.SettingsPath,
        """{"Roots":[],"LinkedSites":[],"Tld":"test"}"""
    );
    var migratedLegacySettings = legacyStore.Load();
    Check(
        migratedLegacySettings.OnboardingCompleted,
        "settings created before onboarding remain completed"
    );
    Check(
        migratedLegacySettings.SchemaVersion == SiteConfigurationStore.CurrentSchemaVersion
            && File.ReadAllText(legacyStore.SettingsPath).Contains(
                $"\"SchemaVersion\": {SiteConfigurationStore.CurrentSchemaVersion}",
                StringComparison.Ordinal
            ),
        "legacy Windows site settings are migrated and persisted"
    );
    Check(
        InitialSetupStages.Installation.SequenceEqual([
            InitialSetupStage.LocalDomains,
            InitialSetupStage.Certificate,
            InitialSetupStage.Php,
            InitialSetupStage.Composer,
            InitialSetupStage.Node,
            InitialSetupStage.Finishing
        ]),
        "initial setup stages keep dependency order"
    );

    var corruptSiteStore = new SiteConfigurationStore(Path.Combine(supportRoot, "corrupt-sites"));
    Directory.CreateDirectory(Path.GetDirectoryName(corruptSiteStore.SettingsPath)!);
    const string corruptSiteJson = "{ invalid site settings";
    File.WriteAllText(corruptSiteStore.SettingsPath, corruptSiteJson);
    var recoveredSiteSettings = corruptSiteStore.Load();
    Check(!recoveredSiteSettings.OnboardingCompleted, "corrupt site settings fall back without completing setup");
    Check(!File.Exists(corruptSiteStore.SettingsPath), "corrupt site settings are not left to be overwritten");
    Check(
        corruptSiteStore.LastBackupPath is { } siteBackup
            && File.ReadAllText(siteBackup) == corruptSiteJson
            && corruptSiteStore.LastLoadWarning?.Contains(siteBackup, StringComparison.Ordinal) == true,
        "corrupt site settings are preserved and reported"
    );

    var futureSiteStore = new SiteConfigurationStore(Path.Combine(supportRoot, "future-sites"));
    Directory.CreateDirectory(Path.GetDirectoryName(futureSiteStore.SettingsPath)!);
    var futureSchemaVersion = SiteConfigurationStore.CurrentSchemaVersion + 1;
    var futureSiteJson =
        $"{{\"SchemaVersion\":{futureSchemaVersion},\"Roots\":[\"future\"],\"FutureOnly\":\"preserve\"}}";
    File.WriteAllText(futureSiteStore.SettingsPath, futureSiteJson);
    var futureFallback = futureSiteStore.Load();
    Check(
        futureFallback.SchemaVersion == SiteConfigurationStore.CurrentSchemaVersion,
        "future Windows site settings fall back to the supported schema"
    );
    Check(
        !File.Exists(futureSiteStore.SettingsPath)
            && futureSiteStore.LastBackupPath is { } futureBackup
            && File.ReadAllText(futureBackup) == futureSiteJson
            && futureSiteStore.LastLoadWarning?.Contains("newer release", StringComparison.Ordinal) == true,
        "future Windows site settings are preserved and never replaced"
    );

    var corruptServices = new WindowsServiceManager(Path.Combine(supportRoot, "corrupt-services"));
    Directory.CreateDirectory(Path.GetDirectoryName(corruptServices.ConfigurationPath)!);
    const string corruptServicesJson = "{ invalid service settings";
    File.WriteAllText(corruptServices.ConfigurationPath, corruptServicesJson);
    Check(corruptServices.LoadInstances().Count == 0, "corrupt service settings fall back to an empty list");
    Check(!File.Exists(corruptServices.ConfigurationPath), "corrupt service settings are not left to be overwritten");
    Check(
        corruptServices.LastBackupPath is { } serviceBackup
            && File.ReadAllText(serviceBackup) == corruptServicesJson
            && corruptServices.LastLoadWarning?.Contains(serviceBackup, StringComparison.Ordinal) == true,
        "corrupt service settings are preserved and reported"
    );

    var store = new SiteConfigurationStore(supportRoot);
    var root = Path.Combine(supportRoot, "Sites");
    var linked = Path.Combine(supportRoot, "Linked");
    store.Save(new WindowsSiteSettings
    {
        Roots = [root, root, "  "],
        LinkedSites = [linked, linked],
        Tld = " Local-Test. ",
        StartAutomatically = true,
        ShowPreviews = false,
        AutomaticUpdates = false,
        UpdateChannel = "beta",
        OnboardingCompleted = true
    });
    var settings = store.Load();
    Check(settings.Roots.SequenceEqual([Path.GetFullPath(root)]), "site roots are normalized");
    Check(settings.LinkedSites.SequenceEqual([Path.GetFullPath(linked)]), "linked sites are normalized");
    Check(settings.Tld == "local-test", "TLD is normalized");
    Check(settings.StartAutomatically, "automatic site startup is persisted");
    Check(!settings.ShowPreviews, "preview preference is persisted");
    Check(!settings.AutomaticUpdates, "automatic update preference is persisted");
    Check(settings.UpdateChannel == "Beta", "update channel is normalized");
    Check(settings.OnboardingCompleted, "completed initial setup is persisted");
    Check(
        settings.SchemaVersion == SiteConfigurationStore.CurrentSchemaVersion,
        "saved Windows site settings use the current schema"
    );
    var refusedFutureSave = false;
    try
    {
        store.Save(new WindowsSiteSettings
        {
            SchemaVersion = SiteConfigurationStore.CurrentSchemaVersion + 1
        });
    }
    catch (InvalidOperationException)
    {
        refusedFutureSave = true;
    }
    Check(refusedFutureSave, "Windows refuses to overwrite settings with a future schema");
    var independentHome = Path.Combine(supportRoot, "independent-home");
    var otherHerd = Path.Combine(independentHome, "Herd");
    var otherHerdProject = Path.Combine(otherHerd, "project");
    var independentProjects = Path.Combine(independentHome, "HerdMe");
    var otherLocalData = Path.Combine(supportRoot, "other-local-data");
    var otherRoamingData = Path.Combine(supportRoot, "other-roaming-data");
    Check(
        SiteConfigurationStore.BelongsToOtherHerd(
            otherHerdProject,
            independentHome,
            otherLocalData,
            otherRoamingData
        ),
        "other Herd project folders are rejected"
    );
    Check(
        SiteConfigurationStore.BelongsToOtherHerd(
            Path.Combine(otherLocalData, "Herd", "bin", "php.exe"),
            independentHome,
            otherLocalData,
            otherRoamingData
        ),
        "other Herd private runtime folders are rejected"
    );
    Check(
        !SiteConfigurationStore.BelongsToOtherHerd(
            independentProjects,
            independentHome,
            otherLocalData,
            otherRoamingData
        ),
        "HerdMe-owned project folders remain accepted"
    );
    var independentSettings = SiteConfigurationStore.Normalize(
        new WindowsSiteSettings
        {
            Roots = [otherHerd, otherHerdProject, independentProjects],
            LinkedSites = [otherHerdProject, independentProjects]
        },
        independentHome,
        otherLocalData,
        otherRoamingData
    );
    Check(
        independentSettings.Roots.SequenceEqual([Path.GetFullPath(independentProjects)]),
        "stored park roots exclude other Herd folders"
    );
    Check(
        independentSettings.LinkedSites.SequenceEqual([Path.GetFullPath(independentProjects)]),
        "stored linked projects exclude other Herd folders"
    );
    store.UpdateSites([Path.Combine(supportRoot, "Other Sites")], "dev-test", showPreviews: true);
    var siteUpdatedSettings = store.Load();
    Check(!siteUpdatedSettings.AutomaticUpdates, "site edits preserve automatic update preferences");
    Check(siteUpdatedSettings.UpdateChannel == "Beta", "site edits preserve the update channel");
    Check(siteUpdatedSettings.StartAutomatically, "site edits preserve automatic environment startup");
    Check(siteUpdatedSettings.ShowPreviews, "site edits apply preview preferences");
    Check(siteUpdatedSettings.Tld == "dev-test", "site edits apply the local TLD");

    var site = new SiteRecord
    {
        Name = "Demo API",
        Domain = "demo-api.local-test",
        Path = Path.Combine(root, "demo-api"),
        Framework = "Laravel",
        Linked = true,
        PhpVersion = "8.4",
        NodeVersion = "24"
    };
    var other = new SiteRecord
    {
        Name = "Store",
        Domain = "store.local-test",
        Path = Path.Combine(root, "store"),
        Framework = "Node.js"
    };
    var routingKey = WindowsLocalEnvironment.ConfigurationKey([site, other], "8.4");
    Check(
        routingKey == WindowsLocalEnvironment.ConfigurationKey([other, site], "8.4"),
        "site environment configuration is independent of scan order"
    );
    var equivalentSite = new SiteRecord
    {
        Name = site.Name,
        Domain = "DEMO-API.LOCAL-TEST.",
        Path = site.Path.ToUpperInvariant(),
        Framework = site.Framework,
        Linked = site.Linked,
        PhpVersion = site.PhpVersion,
        NodeVersion = site.NodeVersion
    };
    Check(
        routingKey == WindowsLocalEnvironment.ConfigurationKey([equivalentSite, other], "8.4"),
        "site environment configuration normalizes Windows domains and paths"
    );
    var differentPhpSite = new SiteRecord
    {
        Name = site.Name,
        Domain = site.Domain,
        Path = site.Path,
        Framework = site.Framework,
        Linked = site.Linked,
        PhpVersion = "8.3",
        NodeVersion = site.NodeVersion
    };
    Check(
        routingKey != WindowsLocalEnvironment.ConfigurationKey([differentPhpSite, other], "8.4"),
        "site environment configuration changes with the selected PHP runtime"
    );
    Check(
        routingKey != WindowsLocalEnvironment.ConfigurationKey([site, other], "8.3"),
        "site environment configuration changes with the default PHP runtime"
    );
    Check(
        WindowsCertificateManager.NormalizeDomains([
            " B.TEST. ",
            "a.test",
            "b.test",
            "not a domain"
        ]).SequenceEqual(["a.test", "b.test"]),
        "Windows certificate domains are validated, normalized, and sorted"
    );
    Check(
        WindowsCertificateManager.ServerCertificateCacheKey(["b.test", "A.TEST."])
            == WindowsCertificateManager.ServerCertificateCacheKey(["a.test", "b.test"]),
        "Windows server certificate reuse is independent of domain order and case"
    );
    Check(
        WindowsCertificateManager.ServerCertificateCacheKey(["a.test"])
            != WindowsCertificateManager.ServerCertificateCacheKey(["a.test", "b.test"]),
        "Windows server certificates are renewed when the domain set changes"
    );
    Check(
        WindowsCredentialStore.BuildTarget(
            "ManagedServices/v1",
            "67de23e7-a688-4402-818f-37149b45ff86"
        ) == "HerdMe/ManagedServices/v1/67de23e7-a688-4402-818f-37149b45ff86",
        "the shared Windows credential store preserves managed-service target names"
    );
    Throws<ArgumentException>(
        () => WindowsCredentialStore.BuildTarget("Certificates/v1", "../authority"),
        "Windows credential account names reject separators and traversal"
    );
    var authorityAccount = WindowsCertificateManager.PasswordAccount(
        WindowsCertificateManager.AuthorityCredentialKind,
        new byte[] { 1, 2, 3, 4 }
    );
    Check(
        authorityAccount.StartsWith("authority-pfx-password-", StringComparison.Ordinal)
            && authorityAccount.Length == "authority-pfx-password-".Length + 64
            && authorityAccount == WindowsCertificateManager.PasswordAccount(
                WindowsCertificateManager.AuthorityCredentialKind,
                new byte[] { 1, 2, 3, 4 }
            )
            && authorityAccount != WindowsCertificateManager.PasswordAccount(
                WindowsCertificateManager.AuthorityCredentialKind,
                new byte[] { 1, 2, 3, 5 }
            ),
        "certificate credential accounts are deterministic and bound to PFX contents"
    );

    var credentialMigrationRoot = Path.Combine(supportRoot, "credential-migration");
    Directory.CreateDirectory(credentialMigrationRoot);
    var credentialBackend = new MemoryCredentialBackend();
    var certificateCredentialStore = new WindowsCredentialStore(
        WindowsCertificateManager.CredentialScope,
        credentialBackend
    );
    var legacyAuthorityPassword = Path.Combine(credentialMigrationRoot, "authority.password");
    File.WriteAllText(legacyAuthorityPassword, "  legacy-authority-password  \n");
    Check(
        certificateCredentialStore.ReadOrMigrate(authorityAccount, legacyAuthorityPassword)
            == "legacy-authority-password"
            && !File.Exists(legacyAuthorityPassword)
            && credentialBackend.Secrets[
                WindowsCredentialStore.BuildTarget(
                    WindowsCertificateManager.CredentialScope,
                    authorityAccount
                )
            ] == "legacy-authority-password",
        "legacy certificate passwords are verified in Credential Manager before plaintext deletion"
    );

    var failedMigrationPath = Path.Combine(credentialMigrationRoot, "server.password");
    File.WriteAllText(failedMigrationPath, "server-password-that-must-remain");
    credentialBackend.FailWrites = true;
    Throws<IOException>(
        () => certificateCredentialStore.ReadOrMigrate(
            WindowsCertificateManager.PasswordAccount(
                WindowsCertificateManager.ServerCredentialKind,
                new byte[] { 9, 8, 7, 6 }
            ),
            failedMigrationPath
        ),
        "failed Windows credential migration is reported"
    );
    credentialBackend.FailWrites = false;
    Check(
        File.ReadAllText(failedMigrationPath) == "server-password-that-must-remain",
        "failed Windows credential migration keeps the plaintext recovery source"
    );

    var invalidProtectedPath = Path.Combine(credentialMigrationRoot, "invalid-protected.password");
    File.WriteAllText(invalidProtectedPath, "plaintext-recovery-password");
    var invalidProtectedAccount = WindowsCertificateManager.PasswordAccount(
        WindowsCertificateManager.ServerCredentialKind,
        new byte[] { 5, 6, 7, 8 }
    );
    credentialBackend.Secrets[
        WindowsCredentialStore.BuildTarget(
            WindowsCertificateManager.CredentialScope,
            invalidProtectedAccount
        )
    ] = string.Empty;
    Throws<InvalidDataException>(
        () => certificateCredentialStore.ReadOrMigrate(
            invalidProtectedAccount,
            invalidProtectedPath
        ),
        "invalid protected Windows credentials are reported"
    );
    Check(
        File.ReadAllText(invalidProtectedPath) == "plaintext-recovery-password",
        "invalid protected credentials never delete a valid plaintext recovery source"
    );

    var rejectedUnlockPath = Path.Combine(credentialMigrationRoot, "rejected-unlock.password");
    File.WriteAllText(rejectedUnlockPath, "legacy-unlock-password");
    var rejectedUnlockAccount = WindowsCertificateManager.PasswordAccount(
        WindowsCertificateManager.ServerCredentialKind,
        new byte[] { 4, 3, 2, 1 }
    );
    Throws<InvalidDataException>(
        () => certificateCredentialStore.ReadOrMigrate(
            rejectedUnlockAccount,
            rejectedUnlockPath,
            validator: _ => false
        ),
        "certificate migration rejects credentials that cannot unlock their PFX"
    );
    Check(
        File.ReadAllText(rejectedUnlockPath) == "legacy-unlock-password"
            && !credentialBackend.Secrets.ContainsKey(
                WindowsCredentialStore.BuildTarget(
                    WindowsCertificateManager.CredentialScope,
                    rejectedUnlockAccount
                )
            ),
        "failed PFX validation preserves plaintext and rolls back the protected credential"
    );

    var redundantLegacyPath = Path.Combine(credentialMigrationRoot, "redundant.password");
    File.WriteAllText(redundantLegacyPath, "obsolete-plaintext");
    certificateCredentialStore.Write(authorityAccount, "protected-authority-password");
    Check(
        certificateCredentialStore.ReadOrMigrate(authorityAccount, redundantLegacyPath)
            == "protected-authority-password"
            && !File.Exists(redundantLegacyPath),
        "verified protected credentials remove leftover plaintext password files"
    );
    Check(SitePresentation.Filter([site, other], "laravel").SequenceEqual([site]), "site search includes framework");
    Check(SitePresentation.Filter([site, other], "STORE").SequenceEqual([other]), "site search is case-insensitive");
    Check(SitePresentation.Filter([site, other], " ").Count == 2, "empty site search returns every site");
    Check(
        SitePresentation.SiteUri(site, false, null, null).AbsoluteUri == "http://demo-api.local-test/",
        "stopped sites use their standard HTTP URL"
    );
    Check(
        SitePresentation.SiteUri(site, true, 8_080, 8_443).AbsoluteUri == "https://demo-api.local-test:8443/",
        "running sites use the active HTTPS port"
    );
    Check(
        SitePresentation.DisplayAddress(site, true, 8_080, 8_443) == "https://demo-api.local-test",
        "displayed site addresses omit the internal proxy port and trailing slash"
    );
    Check(
        SitePresentation.DisplayAddress(site, false, 8_080, null) == "http://demo-api.local-test",
        "displayed site addresses retain the active protocol"
    );
    Check(
        SitePresentation.SiteUri(site, true, 80, 443).AbsoluteUri == "https://demo-api.local-test/",
        "standard HTTPS omits its port"
    );
    Check(
        SitePresentation.DebugUri(site, true, 8_080, 8_443, "PHP STORM").AbsoluteUri
            == "https://demo-api.local-test:8443/?XDEBUG_TRIGGER=PHP%20STORM",
        "debug sessions use the active site URL and escaped IDE trigger"
    );
    Check(SitePresentation.RuntimeLabel(site) == "PHP 8.4  Node.js 24", "site runtime label includes PHP and Node.js");
    using (var metrics = JsonDocument.Parse(SitePresentation.DesktopPreviewMetricsJson(720)))
    {
        var rootElement = metrics.RootElement;
        Check(rootElement.GetProperty("width").GetInt32() == 1_440, "site preview uses a desktop viewport width");
        Check(rootElement.GetProperty("height").GetInt32() == 934, "site preview uses a desktop viewport height");
        Check(rootElement.GetProperty("deviceScaleFactor").GetInt32() == 1, "site preview uses a stable device scale");
        Check(!rootElement.GetProperty("mobile").GetBoolean(), "site preview disables mobile emulation");
        Check(rootElement.GetProperty("scale").GetDouble() == 0.5, "site preview fits the desktop viewport to its frame");
    }
    using (var metrics = JsonDocument.Parse(SitePresentation.DesktopPreviewMetricsJson(10_000)))
    {
        Check(metrics.RootElement.GetProperty("scale").GetDouble() == 1.0, "site preview never enlarges desktop pages");
    }
    using (var metrics = JsonDocument.Parse(SitePresentation.DesktopPreviewMetricsJson(0)))
    {
        Check(metrics.RootElement.GetProperty("scale").GetDouble() == 1.0, "site preview handles an unavailable frame width");
    }

    var logContent = "[Info] Started\r\n[Error] Connection failed\r\n[Info] Retrying";
    Check(
        LogPresentation.FilterLines(logContent, "ERROR") == "[Error] Connection failed",
        "log search filters lines without case sensitivity"
    );
    Check(
        LogPresentation.FilterLines(logContent, " ") == logContent,
        "an empty log search preserves the original content"
    );
    Check(
        LogPresentation.SiteLogRoot(Path.Combine(supportRoot, "demo"))
            == Path.Combine(supportRoot, "demo", "storage", "logs"),
        "Laravel logs resolve inside the selected site's storage directory"
    );

    Check(ManagedServiceCatalog.Get("mongodb").DefaultPort == 27_017, "Windows service catalog includes MongoDB");
    Check(ManagedServiceCatalog.Get("mysql").DefaultPort == 3_306, "Windows service catalog includes MySQL");
    Check(ManagedServiceCatalog.Get("postgresql").DefaultPort == 5_432, "Windows service catalog includes PostgreSQL");
    Check(ManagedServiceCatalog.Get("redis").DefaultPort == 6_379, "Windows service catalog includes Redis");
    Check(ManagedServiceCatalog.Get("rustfs").DefaultPort == 9_000, "Windows service catalog includes RustFS");
    var valkeyDefinition = ManagedServiceCatalog.Get("valkey");
    Check(valkeyDefinition.DefaultPort == 6_379, "Windows service catalog includes Valkey");
    Check(!valkeyDefinition.IsInstallable, "Valkey is disabled without an official native Windows package");
    Check(
        !string.IsNullOrWhiteSpace(valkeyDefinition.UnavailableReason),
        "Valkey explains why native Windows installation is unavailable"
    );
    var typesenseDefinition = ManagedServiceCatalog.Get("typesense");
    Check(typesenseDefinition.DefaultPort == 8_108, "Windows service catalog includes Typesense");
    Check(!typesenseDefinition.IsInstallable, "Typesense is disabled without an official native Windows package");
    Check(
        !string.IsNullOrWhiteSpace(typesenseDefinition.UnavailableReason),
        "Typesense explains why native Windows installation is unavailable"
    );
    Check(
        WindowsServiceManager.AvailablePort(
            3_306,
            new HashSet<int> { 3_306, 3_307 },
            port => port != 3_308
        ) == 3_309,
        "service port selection skips configured and externally unavailable ports"
    );
    Check(
        WindowsServiceManager.AvailablePort(
            65_535,
            new HashSet<int> { 65_535, 1_024 },
            _ => true
        ) == 1_025,
        "service port selection wraps safely after the final TCP port"
    );
    var externalServiceListener = new TcpListener(IPAddress.Loopback, 0);
    externalServiceListener.Start();
    try
    {
        var externalPort = ((IPEndPoint)externalServiceListener.LocalEndpoint).Port;
        var suggestedPort = WindowsServiceManager.AvailablePort(externalPort);
        Check(
            suggestedPort is not null
                && suggestedPort != externalPort
                && WindowsServiceManager.IsPortAvailable(suggestedPort.Value),
            "service port selection proposes a free loopback port after an external conflict"
        );
        using var externalProbe = new TcpClient();
        await externalProbe.ConnectAsync(IPAddress.Loopback, externalPort)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Check(
            externalProbe.Connected,
            "service port recovery never terminates the external port owner"
        );
    }
    finally
    {
        externalServiceListener.Stop();
    }

    var environmentProject = Path.Combine(supportRoot, "environment-project");
    Directory.CreateDirectory(environmentProject);
    await File.WriteAllTextAsync(
        Path.Combine(environmentProject, ".env.example"),
        "APP_NAME=Example\r\n# Keep this comment\r\nDB_HOST=localhost\r\n"
    );
    var environmentInstance = new ManagedServiceInstance
    {
        DefinitionId = "mysql",
        Name = "Local MySQL",
        Port = 3_306
    };
    var environmentCredentials = new ServiceCredentials(
        "herdme_testuser",
        "test_secret_0123456789_ABCDEFGHIJKLMNOP"
    );
    var firstEnvironmentUpdate = ServiceEnvironmentFile.Update(
        environmentProject,
        environmentInstance,
        environmentCredentials
    );
    Check(firstEnvironmentUpdate.CreatedFile, "service .env updates start from .env.example");
    Check(
        firstEnvironmentUpdate.UpdatedKeys == 1 && firstEnvironmentUpdate.AddedKeys == 5,
        "service .env updates report replaced and appended variables"
    );
    environmentInstance.Port = 3_307;
    var secondEnvironmentUpdate = ServiceEnvironmentFile.Update(
        environmentProject,
        environmentInstance,
        environmentCredentials
    );
    var environmentContents = await File.ReadAllTextAsync(
        Path.Combine(environmentProject, ".env")
    );
    Check(
        !secondEnvironmentUpdate.CreatedFile
            && secondEnvironmentUpdate.UpdatedKeys == 6
            && secondEnvironmentUpdate.AddedKeys == 0,
        "service .env updates replace existing variables without appending duplicates"
    );
    Check(
        environmentContents.Contains("# Keep this comment\r\n", StringComparison.Ordinal)
            && environmentContents.Contains("DB_PORT=3307\r\n", StringComparison.Ordinal),
        "service .env updates preserve CRLF and unrelated comments"
    );
    Check(
        environmentContents.Split("\r\n").Count(line => line.StartsWith("DB_HOST=", StringComparison.Ordinal)) == 1,
        "service .env updates keep one effective assignment per managed key"
    );
    var rustFsEnvironment = ServiceEnvironmentConfiguration.Variables(
        new ManagedServiceInstance
        {
            DefinitionId = "rustfs",
            Name = "RustFS",
            Port = 9_000
        },
        environmentCredentials
    ).ToDictionary(variable => variable.Key, variable => variable.Value);
    Check(
        rustFsEnvironment["AWS_ACCESS_KEY_ID"] == environmentCredentials.Username
            && rustFsEnvironment["AWS_SECRET_ACCESS_KEY"] == environmentCredentials.Secret
            && rustFsEnvironment["AWS_ENDPOINT"] == "http://127.0.0.1:9000",
        "RustFS .env variables match its managed local credentials"
    );
    var mysqlEnvironment = ServiceEnvironmentConfiguration.Variables(
        environmentInstance,
        environmentCredentials
    ).ToDictionary(variable => variable.Key, variable => variable.Value);
    Check(
        mysqlEnvironment["DB_USERNAME"] == environmentCredentials.Username
            && mysqlEnvironment["DB_PASSWORD"] == environmentCredentials.Secret,
        "database .env variables use the managed credentials"
    );
    foreach (var definition in ManagedServiceCatalog.All)
    {
        Check(
            ServiceEnvironmentConfiguration.Variables(
                new ManagedServiceInstance
                {
                    DefinitionId = definition.Id,
                    Name = definition.Name,
                    Port = definition.DefaultPort
                },
                environmentCredentials
            ).Count > 0,
            $"service catalog exposes .env variables for {definition.Id}"
        );
    }
    var unsupportedInstaller = new ServicePackageInstaller();
    foreach (var definition in new[] { valkeyDefinition, typesenseDefinition })
    {
        try
        {
            _ = unsupportedInstaller.ResolveReleaseAsync(definition.Id);
            Check(false, $"{definition.Name} package resolution rejects unavailable native Windows packages");
        }
        catch (NotSupportedException error)
        {
            Check(
                error.Message == definition.UnavailableReason,
                $"{definition.Name} package resolution returns its availability reason"
            );
        }
    }
    var mongoMetadata = """
        {
          "versions": [
            {
              "version": "8.0.27",
              "development_release": false,
              "downloads": [{
                "arch": "x86_64",
                "target": "windows",
                "edition": "base",
                "archive": {
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "url": "https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-8.0.27.zip"
                }
              }]
            },
            {
              "version": "8.0.28",
              "development_release": false,
              "downloads": [{
                "arch": "x86_64",
                "target": "windows",
                "edition": "base",
                "archive": {
                  "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "url": "https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-8.0.28.zip"
                }
              }]
            }
          ]
        }
        """;
    var mongoRelease = ServicePackageInstaller.SelectMongoDbRelease(mongoMetadata);
    Check(mongoRelease.Version == "8.0.28", "MongoDB resolver selects the latest 8.0 LTS release");
    Check(mongoRelease.IsZipArchive, "MongoDB resolver selects the official ZIP package");
    Check(
        mongoRelease.DownloadUri.Host == "fastdl.mongodb.org",
        "MongoDB resolver accepts only the official Community download host"
    );
    var mysqlMetadata = """
        <tr><td class="sub-text">(mysql-9.7.0-winx64.zip)</td>
        <td>MD5: <code class="md5">11111111111111111111111111111111</code></td></tr>
        <tr><td class="sub-text">(mysql-9.7.1-winx64.zip)</td>
        <td>MD5: <code class="md5">8778341c62eb2ab1a95b1f22bee70f9e</code></td></tr>
        <tr><td class="sub-text">(mysql-9.7.1-winx64-debug-test.zip)</td>
        <td>MD5: <code class="md5">22222222222222222222222222222222</code></td></tr>
        """;
    var mysqlRelease = ServicePackageInstaller.SelectMySqlRelease(mysqlMetadata);
    Check(mysqlRelease.Version == "9.7.1", "MySQL resolver selects the latest Windows x64 ZIP");
    Check(
        mysqlRelease.ChecksumAlgorithm == ServicePackageChecksumAlgorithm.Md5,
        "MySQL resolver records the checksum algorithm published by the vendor"
    );
    Check(
        mysqlRelease.Checksum == "8778341c62eb2ab1a95b1f22bee70f9e",
        "MySQL resolver preserves the published vendor checksum"
    );
    Check(
        mysqlRelease.DownloadUri.AbsoluteUri
            == "https://cdn.mysql.com/Downloads/MySQL-9.7/mysql-9.7.1-winx64.zip",
        "MySQL resolver constructs only the official CDN URL"
    );
    var postgreSqlRelease = await ServicePackageInstaller.ResolvePostgreSqlReleaseAsync();
    Check(postgreSqlRelease.Version == "18.4", "PostgreSQL resolver pins the verified Windows release");
    Check(
        postgreSqlRelease.ChecksumAlgorithm == ServicePackageChecksumAlgorithm.Sha256
            && postgreSqlRelease.Checksum
                == "02e239529ed7833d169f98d915d3feffe0813264b08b3ae353e78e8b9c97e1a6",
        "PostgreSQL resolver requires the verified full-archive SHA-256"
    );
    Check(
        postgreSqlRelease.DownloadUri.Host == "get.enterprisedb.com",
        "PostgreSQL resolver uses the documented EDB binary host"
    );
    var redisMetadata = """
        {
          "draft": false,
          "tag_name": "8.8.1",
          "assets": [{
            "name": "Redis-8.8.1-Windows-x64-msys2.zip",
            "digest": "sha256:1a0741a8f997a50ad7a32370e9ddf719ed3d5d87701324c57b7b34518b980460",
            "browser_download_url": "https://github.com/redis-windows/redis-windows/releases/download/8.8.1/Redis-8.8.1-Windows-x64-msys2.zip"
          }]
        }
        """;
    var redisRelease = ServicePackageInstaller.SelectRedisRelease(redisMetadata);
    Check(redisRelease.Version == "8.8.1", "Redis resolver preserves the stable release version");
    Check(redisRelease.IsZipArchive, "Redis resolver selects the portable Windows ZIP");
    Check(
        redisRelease.ChecksumAlgorithm == ServicePackageChecksumAlgorithm.Sha256
            && redisRelease.Checksum == "1a0741a8f997a50ad7a32370e9ddf719ed3d5d87701324c57b7b34518b980460",
        "Redis resolver requires the GitHub SHA-256 digest"
    );
    Check(
        redisRelease.DownloadUri.Host == "github.com",
        "Redis resolver accepts only the expected GitHub release host"
    );
    var rustFsMetadata = """
        [{
          "draft": false,
          "tag_name": "1.0.0-beta.11-preview.1",
          "assets": [{
            "name": "rustfs-windows-x86_64-v1.0.0-beta.11-preview.1.zip",
            "digest": "sha256:9e60cd6a71e8c18e80a94890576fd62cd55ade67c1476381f931ae7fc294f83a",
            "browser_download_url": "https://github.com/rustfs/rustfs/releases/download/1.0.0-beta.11-preview.1/rustfs-windows-x86_64-v1.0.0-beta.11-preview.1.zip"
          }]
        }]
        """;
    var rustFsRelease = ServicePackageInstaller.SelectRustFsRelease(rustFsMetadata);
    Check(rustFsRelease.Version == "1.0.0-beta.11-preview.1", "RustFS resolver preserves its release version");
    Check(rustFsRelease.IsZipArchive, "RustFS resolver selects the official Windows ZIP");
    Check(
        rustFsRelease.ChecksumAlgorithm == ServicePackageChecksumAlgorithm.Sha256
            && rustFsRelease.Checksum == "9e60cd6a71e8c18e80a94890576fd62cd55ade67c1476381f931ae7fc294f83a",
        "RustFS resolver requires the official GitHub SHA-256 digest"
    );
    var rootArchive = Path.Combine(supportRoot, "root-archive");
    Directory.CreateDirectory(rootArchive);
    await File.WriteAllBytesAsync(Path.Combine(rootArchive, "rustfs.exe"), []);
    var normalizedRustFs = ServicePackageInstaller.NormalizeExtractedRuntime(rootArchive, "rustfs");
    Check(
        File.Exists(Path.Combine(normalizedRustFs, "rustfs.exe")),
        "service ZIP normalization accepts an executable at the archive root"
    );
    var postgreSqlArchive = Path.Combine(supportRoot, "postgresql-archive");
    var postgreSqlBin = Path.Combine(postgreSqlArchive, "pgsql", "bin");
    Directory.CreateDirectory(postgreSqlBin);
    await File.WriteAllBytesAsync(Path.Combine(postgreSqlBin, "postgres.exe"), []);
    var normalizedPostgreSql = ServicePackageInstaller.NormalizeExtractedRuntime(
        postgreSqlArchive,
        "postgresql"
    );
    Check(
        File.Exists(Path.Combine(normalizedPostgreSql, "bin", "postgres.exe")),
        "service ZIP normalization accepts PostgreSQL's pgsql archive root"
    );
    var mongoInstance = new ManagedServiceInstance
    {
        DefinitionId = "mongodb",
        Name = "MongoDB",
        Port = 27_018
    };
    var mongoExecutable = Path.Combine("C:\\HerdMe", "mongodb", "bin", "mongod.exe");
    var mongoData = Path.Combine("C:\\HerdMe", "Services", "mongo", "data");
    var mongoLaunch = WindowsServiceManager.BuildLaunchSpec(mongoInstance, mongoExecutable, mongoData);
    Check(mongoLaunch.Arguments.SequenceEqual([
        "--dbpath", mongoData, "--bind_ip", "127.0.0.1", "--port", "27018"
    ]), "MongoDB launch is loopback-only and uses the configured data and port");
    var mysqlInstance = new ManagedServiceInstance
    {
        DefinitionId = "mysql",
        Name = "MySQL",
        Port = 3_307
    };
    var mysqlExecutable = Path.Combine("C:\\HerdMe", "mysql", "bin", "mysqld.exe");
    var mysqlData = Path.Combine("C:\\HerdMe", "Services", "mysql", "data");
    var mysqlRuntime = Directory.GetParent(Path.GetDirectoryName(mysqlExecutable)!)!.FullName;
    var mysqlLaunch = WindowsServiceManager.BuildLaunchSpec(mysqlInstance, mysqlExecutable, mysqlData);
    Check(mysqlLaunch.Arguments.SequenceEqual([
        "--no-defaults",
        "--console",
        "--basedir=" + mysqlRuntime,
        "--datadir=" + mysqlData,
        "--port=3307",
        "--bind-address=127.0.0.1",
        "--mysqlx=0",
        "--skip-name-resolve",
        "--pid-file=" + Path.Combine(mysqlData, "mysql.pid")
    ]), "MySQL launch disables its extra listener and binds its SQL port to loopback");
    var postgreSqlInstance = new ManagedServiceInstance
    {
        DefinitionId = "postgresql",
        Name = "PostgreSQL",
        Port = 5_433
    };
    var postgreSqlExecutable = Path.Combine(
        "C:\\HerdMe", "postgresql", "bin", "postgres.exe"
    );
    var postgreSqlData = Path.Combine("C:\\HerdMe", "Services", "postgresql", "data");
    var postgreSqlLaunch = WindowsServiceManager.BuildLaunchSpec(
        postgreSqlInstance,
        postgreSqlExecutable,
        postgreSqlData
    );
    Check(postgreSqlLaunch.Arguments.SequenceEqual([
        "-D", postgreSqlData,
        "-h", "127.0.0.1",
        "-p", "5433"
    ]), "PostgreSQL launch binds only to loopback and uses its isolated data directory");
    var redisInstance = new ManagedServiceInstance
    {
        DefinitionId = "redis",
        Name = "Redis",
        Port = 6_380
    };
    var redisData = Path.Combine("C:\\HerdMe", "Services", "redis", "data");
    var redisLaunch = WindowsServiceManager.BuildLaunchSpec(
        redisInstance,
        Path.Combine("C:\\HerdMe", "redis", "redis-server.exe"),
        redisData
    );
    Check(
        redisLaunch.Arguments.SequenceEqual([
            "--bind", "127.0.0.1",
            "--port", "6380",
            "--dir", redisData.Replace('\\', '/'),
            "--protected-mode", "yes",
            "--appendonly", "yes",
            "--daemonize", "no"
        ]),
        "Redis launch is persistent, protected, and loopback-only"
    );
    var rustFsInstance = new ManagedServiceInstance
    {
        DefinitionId = "rustfs",
        Name = "RustFS",
        Port = 9_001
    };
    var rustFsData = Path.Combine("C:\\HerdMe", "Services", "rustfs", "data");
    var rustFsCredentials = ServiceCredentialGenerator.Create(rustFsInstance.Id);
    var otherCredentials = ServiceCredentialGenerator.Create(Guid.NewGuid());
    Check(
        rustFsCredentials.IsValid
            && otherCredentials.IsValid
            && rustFsCredentials.Secret != otherCredentials.Secret
            && rustFsCredentials.Username != otherCredentials.Username
            && !rustFsCredentials.Secret.Contains("herdme-local-service", StringComparison.Ordinal),
        "managed service credentials are random, valid, and unique per instance"
    );
    var rustFsLaunch = WindowsServiceManager.BuildLaunchSpec(
        rustFsInstance,
        Path.Combine("C:\\HerdMe", "rustfs", "rustfs.exe"),
        rustFsData,
        9_002,
        rustFsCredentials
    );
    Check(
        rustFsLaunch.Arguments.SequenceEqual([
            "server", rustFsData,
            "--address", "127.0.0.1:9001",
            "--console-address", "127.0.0.1:9002"
        ]),
        "RustFS launch binds its API and console to loopback"
    );
    Check(
        rustFsLaunch.Environment["RUSTFS_ACCESS_KEY"] == rustFsCredentials.Username
            && rustFsLaunch.Environment["RUSTFS_SECRET_KEY"] == rustFsCredentials.Secret,
        "RustFS launch credentials match generated .env values"
    );
    var rustFsLaunchEnvironment = ServiceEnvironmentConfiguration.Variables(
        rustFsInstance,
        rustFsCredentials
    ).ToDictionary(variable => variable.Key, variable => variable.Value);
    Check(
        rustFsLaunch.Environment["RUSTFS_ACCESS_KEY"]
                == rustFsLaunchEnvironment["AWS_ACCESS_KEY_ID"]
            && rustFsLaunch.Environment["RUSTFS_SECRET_KEY"]
                == rustFsLaunchEnvironment["AWS_SECRET_ACCESS_KEY"],
        "RustFS launch and .env export use exactly the same managed credentials"
    );
    var tablePlusMySql = TablePlusConnection.UriFor(
        new ManagedServiceInstance
        {
            DefinitionId = "mysql",
            Name = "MySQL",
            Port = 3_307
        },
        environmentCredentials
    );
    var tablePlusPostgreSql = TablePlusConnection.UriFor(
        new ManagedServiceInstance
        {
            DefinitionId = "postgresql",
            Name = "PostgreSQL",
            Port = 5_433
        },
        environmentCredentials
    );
    var tablePlusRedis = TablePlusConnection.UriFor(new ManagedServiceInstance
    {
        DefinitionId = "valkey",
        Name = "Valkey",
        Port = 6_380
    });
    Check(
        tablePlusMySql is not null
            && tablePlusMySql.Scheme == "mysql"
            && tablePlusMySql.UserInfo.Contains(environmentCredentials.Username, StringComparison.Ordinal)
            && tablePlusMySql.UserInfo.Contains(environmentCredentials.Secret, StringComparison.Ordinal)
            && tablePlusMySql.Host == "127.0.0.1"
            && tablePlusMySql.Port == 3_307
            && tablePlusMySql.AbsolutePath == "/mysql",
        "Windows TablePlus MySQL links use the managed loopback connection"
    );
    Check(
        TablePlusConnection.DisplayAddress(new ManagedServiceInstance
        {
            DefinitionId = "mysql",
            Name = "MySQL",
            Port = 3_307
        }) == "mysql://127.0.0.1:3307/mysql"
            && !TablePlusConnection.DisplayAddress(new ManagedServiceInstance
            {
                DefinitionId = "mysql",
                Name = "MySQL",
                Port = 3_307
            })!.Contains(environmentCredentials.Secret, StringComparison.Ordinal),
        "Windows service rows reveal a password-free database connection address"
    );
    Check(
        tablePlusPostgreSql is not null
            && tablePlusPostgreSql.Scheme == "postgresql"
            && tablePlusPostgreSql.UserInfo.Contains(environmentCredentials.Username, StringComparison.Ordinal)
            && tablePlusPostgreSql.UserInfo.Contains(environmentCredentials.Secret, StringComparison.Ordinal)
            && tablePlusPostgreSql.Port == 5_433
            && tablePlusPostgreSql.AbsolutePath == "/postgres",
        "Windows TablePlus PostgreSQL links preserve the managed username"
    );
    Check(
        tablePlusRedis is not null
            && tablePlusRedis.Scheme == "redis"
            && tablePlusRedis.Port == 6_380
            && tablePlusRedis.AbsolutePath == "/0"
            && TablePlusConnection.UriFor(new ManagedServiceInstance
            {
                DefinitionId = "minio",
                Name = "MinIO",
                Port = 9_000
            }) is null,
        "Windows TablePlus links cover cache services and reject unsupported services"
    );
    Check(
        TablePlusConnection.DisplayAddress(new ManagedServiceInstance
        {
            DefinitionId = "minio",
            Name = "MinIO",
            Port = 9_000
        }) is null,
        "Windows service rows hide connection addresses for unsupported services"
    );
    Check(
        TablePlusConnection.UriFor(new ManagedServiceInstance
        {
            DefinitionId = "mysql",
            Name = "MySQL",
            Port = 3_306
        }) is null,
        "database TablePlus links require managed credentials"
    );
    var mysqlProvisioningSql = DatabaseServiceAuthenticator.MySqlProvisioningSql(
        environmentCredentials
    );
    Check(
        mysqlProvisioningSql.Contains(
            $"'{environmentCredentials.Username}'@'127.0.0.1'",
            StringComparison.Ordinal
        )
            && mysqlProvisioningSql.Contains(
                $"IDENTIFIED BY '{environmentCredentials.Secret}'",
                StringComparison.Ordinal
            )
            && mysqlProvisioningSql.Contains("ALTER USER 'root'@'localhost'", StringComparison.Ordinal)
            && mysqlProvisioningSql.Contains("DELETE FROM mysql.user WHERE User = ''", StringComparison.Ordinal),
        "MySQL provisioning creates managed credentials and removes passwordless accounts"
    );
    var securedHba = DatabaseServiceAuthenticator.SecurePostgreSqlHba(
        "# keep\r\nlocal all all trust\r\nhost all all 127.0.0.1/32 trust # loopback\r\nhostssl all all ::1/128 scram-sha-256\r\n"
    );
    Check(
        securedHba.Changed
            && !securedHba.Contents.Contains(" all trust", StringComparison.Ordinal)
            && securedHba.Contents.Contains("scram-sha-256 # loopback", StringComparison.Ordinal)
            && securedHba.Contents.Contains("# keep\r\n", StringComparison.Ordinal),
        "PostgreSQL migration replaces trust rules while preserving comments and CRLF"
    );
    var currentServiceRow = new ManagedServiceRow
    {
        Id = Guid.NewGuid(),
        DefinitionId = "mongodb",
        Name = "MongoDB",
        Port = 27_017,
        Version = "8.0.28",
        State = ManagedServiceState.Stopped,
        StartAutomatically = false,
        IsUpdateAvailable = false
    };
    Check(!currentServiceRow.CanInstallOrUpdate, "current service releases hide the update button");
    Check(!currentServiceRow.CanOpenInTablePlus, "stopped databases hide the TablePlus action");
    var outdatedServiceRow = new ManagedServiceRow
    {
        Id = Guid.NewGuid(),
        DefinitionId = "mongodb",
        Name = "MongoDB",
        Port = 27_017,
        Version = "8.0.27",
        State = ManagedServiceState.Stopped,
        StartAutomatically = false,
        IsUpdateAvailable = true
    };
    Check(outdatedServiceRow.CanInstallOrUpdate, "outdated service releases expose the update button");
    var runningStorageRow = new ManagedServiceRow
    {
        Id = Guid.NewGuid(),
        DefinitionId = "rustfs",
        Name = "RustFS",
        Port = 9_001,
        Version = "1.0.0-beta.11-preview.1",
        State = ManagedServiceState.Running,
        StartAutomatically = false,
        IsUpdateAvailable = false,
        ConsolePort = 9_002
    };
    Check(runningStorageRow.CanOpenConsole, "running storage services expose their console action");
    Check(!currentServiceRow.CanOpenConsole, "services without a console hide the console action");
    var runningDatabaseRow = new ManagedServiceRow
    {
        Id = Guid.NewGuid(),
        DefinitionId = "mongodb",
        Name = "MongoDB",
        Port = 27_017,
        Version = "8.0.28",
        State = ManagedServiceState.Running,
        StartAutomatically = false,
        IsUpdateAvailable = false
    };
    Check(runningDatabaseRow.CanOpenInTablePlus, "running databases expose the TablePlus action");

    var hosts = WindowsHostsManager.Render(
        "127.0.0.1 localhost\n"
        + "# User entry\n"
        + "# BEGIN HerdMe local sites\n127.0.0.1 old.test\n# END HerdMe local sites\n"
        + "10.0.0.5 intranet.test\n",
        [" Store.Local-Test. ", "api.local-test", "store.local-test", "not a domain"]
    );
    Check(hosts.Contains("# User entry\r\n"), "hosts rendering preserves user entries");
    Check(!hosts.Contains("old.test"), "hosts rendering replaces the previous managed block");
    Check(hosts.Contains("127.0.0.1\tapi.local-test\r\n"), "hosts rendering normalizes valid domains");
    Check(hosts.Split("store.local-test").Length == 2, "hosts rendering removes duplicate domains");
    Check(!hosts.Contains("not a domain"), "hosts rendering rejects invalid domains");
    var hostsWithoutSites = WindowsHostsManager.Render(hosts, []);
    Check(!hostsWithoutSites.Contains("BEGIN HerdMe"), "hosts rendering removes an unused managed block");
    Check(hostsWithoutSites.Contains("10.0.0.5 intranet.test"), "hosts cleanup preserves unrelated mappings");
    Check(WindowsHostsManager.ContainsManagedBlock(hosts), "managed hosts blocks are detected");
    Check(!WindowsHostsManager.ContainsManagedBlock(hostsWithoutSites), "removed hosts blocks are not reported");
    var helperSupport = Path.Combine(supportRoot, "helper");
    var helperWindows = Path.Combine(supportRoot, "windows");
    var allowedSource = Path.Combine(helperSupport, "Cache", "hosts", "hosts-1234");
    var allowedDestination = Path.Combine(helperWindows, "System32", "drivers", "etc", "hosts");
    Check(
        WindowsHostsManager.IsAllowedHelperRequest(
            allowedSource,
            allowedDestination,
            helperSupport,
            helperWindows
        ),
        "the elevated hosts helper accepts only HerdMe's staged hosts file"
    );
    Check(
        !WindowsHostsManager.IsAllowedHelperRequest(
            Path.Combine(supportRoot, "outside-hosts"),
            allowedDestination,
            helperSupport,
            helperWindows
        ),
        "the elevated hosts helper rejects sources outside HerdMe's cache"
    );
    Check(
        !WindowsHostsManager.IsAllowedHelperRequest(
            allowedSource,
            Path.Combine(supportRoot, "unrelated-file"),
            helperSupport,
            helperWindows
        ),
        "the elevated hosts helper rejects destinations other than Windows hosts"
    );

    var runtimeSite = Path.Combine(supportRoot, "RuntimeSite");
    Directory.CreateDirectory(runtimeSite);
    var runtimeStore = new SiteRuntimeStore();
    runtimeStore.SetPhp(runtimeSite, " 8.4 ");
    runtimeStore.SetNode(runtimeSite, "v24.1.0");
    Check(File.ReadAllText(Path.Combine(runtimeSite, ".herdme-php")).Trim() == "8.4", "PHP site runtime is persisted");
    Check(File.ReadAllText(Path.Combine(runtimeSite, ".herdme-node")).Trim() == "v24.1.0", "Node site runtime is persisted");
    Throws<ArgumentException>(() => runtimeStore.SetPhp(runtimeSite, "8.x"), "invalid PHP cycles are rejected");
    Throws<ArgumentException>(() => runtimeStore.SetNode(runtimeSite, "current"), "invalid Node versions are rejected");
    runtimeStore.SetPhp(runtimeSite, null);
    runtimeStore.SetNode(runtimeSite, " ");
    Check(!File.Exists(Path.Combine(runtimeSite, ".herdme-php")), "PHP site runtime can return to its default");
    Check(!File.Exists(Path.Combine(runtimeSite, ".herdme-node")), "Node site runtime can return to its default");

    var message = CapturedMail.Parse(
        "sender@example.test",
        ["recipient@example.test"],
        "From: sender@example.test\r\n"
        + "Subject: =?UTF-8?B?2YXYsdit2KjYpw==?=\r\n"
        + "Content-Type: multipart/alternative; boundary=herdme\r\n\r\n"
        + "--herdme\r\nContent-Type: text/plain; charset=utf-8\r\n"
        + "Content-Transfer-Encoding: quoted-printable\r\n\r\nHello=20mail\r\n"
        + "--herdme\r\nContent-Type: text/html; charset=utf-8\r\n"
        + "Content-Transfer-Encoding: base64\r\n\r\nPGI+SGVsbG8gbWFpbDwvYj4=\r\n"
        + "--herdme--\r\n"
    );
    Check(message.Subject == "مرحبا", "mail headers decode RFC 2047 text");
    Check(message.Body == "Hello mail", "mail quoted-printable text decodes");
    Check(message.MatchesSearch(""), "empty mail searches keep every message");
    Check(message.MatchesSearch("SENDER@EXAMPLE.TEST"), "mail searches match senders without case sensitivity");
    Check(message.MatchesSearch("مرحبا"), "mail searches match decoded subjects");
    Check(message.MatchesSearch("recipient@example.test"), "mail searches match recipients");
    Check(!message.MatchesSearch("Hello mail"), "mail searches do not inspect private message bodies");
    Check(!message.MatchesSearch("does-not-exist"), "mail searches reject unrelated metadata");
    var htmlBody = message.HtmlBody ?? throw new InvalidOperationException("Failed contract: mail HTML is present");
    Check(htmlBody == "<b>Hello mail</b>", "mail Base64 HTML decodes");
    var safeMailPreview = MailMimeParser.SafeHtmlDocument(htmlBody);
    Check(safeMailPreview.Contains("default-src 'none'"), "mail HTML preview blocks external content");
    Check(safeMailPreview.Contains("form-action 'none'"), "mail HTML preview blocks forms");
    Check(safeMailPreview.Contains("frame-src 'none'"), "mail HTML preview blocks frames");
    Check(
        safeMailPreview.Contains("style-src 'sha256-48hOXKVM1rwpXip/9XRIr0XijcrNP/RHiD+a7aSGrzg='")
            && !safeMailPreview.Contains("unsafe-inline"),
        "mail HTML preview allows only HerdMe's hashed stylesheet"
    );
    Check(
        MailMimeParser.IsPreviewNavigationAllowed("about:blank")
            && MailMimeParser.IsPreviewNavigationAllowed("about:blank#message")
            && !MailMimeParser.IsPreviewNavigationAllowed("https://example.test")
            && !MailMimeParser.IsPreviewNavigationAllowed("data:text/html,unsafe"),
        "mail HTML preview navigation remains inside its generated document"
    );

    await TestMailCaptureAsync(supportRoot);
    await TestDumpCaptureAsync(supportRoot);
    await TestFastCgiClientAsync();
    await TestLocalHttpSiteServerAsync(supportRoot);
    await TestCancelledCommandKillsTreeAsync(supportRoot);

    var laravelArguments = LaravelProjectCreator.BuildLaravelArguments(
        new LaravelProjectRequest(
            " demo-app ",
            supportRoot,
            "React",
            "PHPUnit",
            InstallBoost: false,
            InitializeGit: true
        )
    );
    Check(
        laravelArguments.SequenceEqual([
            "new", "demo-app", "--no-interaction", "--no-ansi", "--phpunit", "--react", "--no-node"
        ]),
        "Laravel project arguments preserve the selected starter and test framework"
    );
    var customLaravelArguments = LaravelProjectCreator.BuildLaravelArguments(
        new LaravelProjectRequest(
            "custom-app", supportRoot, "Custom", "Pest",
            InstallBoost: false,
            InitializeGit: false,
            CustomStarterKit: "vendor/community-kit:^2.0"
        )
    );
    Check(
        customLaravelArguments.SequenceEqual([
            "new", "custom-app", "--no-interaction", "--no-ansi", "--pest",
            "--using=vendor/community-kit:^2.0", "--npm"
        ]),
        "Laravel custom starter kits use the official package option"
    );
    var stagingFixture = Path.Combine(supportRoot, ".herdme-create-contract");
    Directory.CreateDirectory(Path.Combine(stagingFixture, "partial", "vendor"));
    var readOnlyFixture = Path.Combine(stagingFixture, "partial", "artisan");
    await File.WriteAllTextAsync(readOnlyFixture, "fixture");
    File.SetAttributes(readOnlyFixture, File.GetAttributes(readOnlyFixture) | FileAttributes.ReadOnly);
    LaravelProjectCreator.DeleteStagingDirectory(stagingFixture);
    Check(!Directory.Exists(stagingFixture), "Laravel project staging cleanup removes read-only partial trees");
    var installerFailure = CommandErrorPresenter.Present(
        """
        {"success":false,"directory":"C:\\\\Users\\\\Demo\\\\HerdMe\\\\demo-app","log":"C:\\\\Temp\\\\laravel-installer.log","tail":"UnexpectedValueException at vendor/monolog/monolog/src/Monolog/Handler/StreamHandler.php:164: The stream could not be opened in append mode."}
        """,
        "Laravel Installer could not finish creating the site."
    );
    Check(
        installerFailure.Message.Contains("could not write", StringComparison.OrdinalIgnoreCase)
            && installerFailure.TechnicalDetails?.Contains("Project folder:", StringComparison.Ordinal) == true
            && !installerFailure.Message.StartsWith('{'),
        "Laravel command failures hide raw JSON behind a concise message"
    );
    var diskFailure = CommandErrorPresenter.Present("write failed: No space left on device");
    Check(
        diskFailure.Message.Contains("free disk space", StringComparison.OrdinalIgnoreCase),
        "Command failures identify insufficient disk space"
    );
    var installedTools = new ComposerToolManager(supportRoot);
    var installedPhp = new PhpRuntimeInstaller(supportRoot: supportRoot);
    Directory.CreateDirectory(Path.GetDirectoryName(installedPhp.PhpExecutable("8.4"))!);
    Directory.CreateDirectory(Path.GetDirectoryName(installedTools.ComposerPath)!);
    Directory.CreateDirectory(Path.GetDirectoryName(installedTools.LaravelExecutable)!);
    File.WriteAllText(installedPhp.PhpExecutable("8.4"), "managed php");
    File.WriteAllText(installedPhp.PhpCgiExecutable("8.4"), "managed php-cgi");
    File.WriteAllText(installedTools.ComposerPath, "managed composer");
    File.WriteAllText(installedTools.LaravelExecutable, "managed laravel installer");
    Check(
        installedTools.IsLaravelInstallerReady("8.4"),
        "Project creation detects the installed Laravel Installer without launching it"
    );
    await installedTools.EnsureLaravelInstallerAsync("8.4");
    Check(
        LaravelProjectCreationStages.For(new LaravelProjectRequest(
            "demo-app", supportRoot, "React", "Pest", InstallBoost: true, InitializeGit: true
        )).SequenceEqual([
            LaravelProjectCreationStage.ValidatingRequest,
            LaravelProjectCreationStage.PreparingLaravelInstaller,
            LaravelProjectCreationStage.CreatingLaravelProject,
            LaravelProjectCreationStage.InstallingLaravelBoost,
            LaravelProjectCreationStage.PreparingNodeRuntime,
            LaravelProjectCreationStage.InstallingFrontendDependencies,
            LaravelProjectCreationStage.BuildingFrontendAssets,
            LaravelProjectCreationStage.InitializingGitRepository,
            LaravelProjectCreationStage.VerifyingProject,
            LaravelProjectCreationStage.RegisteringSite,
            LaravelProjectCreationStage.Completed
        ]),
        "Laravel project progress includes selected optional and frontend steps"
    );
    Check(
        LaravelProjectCreationStages.RequiresFrontendAssets(new LaravelProjectRequest(
            "demo-app", supportRoot, "React", "Pest", InstallBoost: false, InitializeGit: false
        )),
        "Laravel React projects require compiled frontend assets"
    );
    Check(
        !LaravelProjectCreationStages.RequiresFrontendAssets(new LaravelProjectRequest(
            "demo-app", supportRoot, "None", "Pest", InstallBoost: false, InitializeGit: false
        )),
        "Laravel projects without a starter kit do not require a Vite build"
    );
    Check(
        LaravelProjectCreationStages.For(new LaravelProjectRequest(
            "demo-app", supportRoot, "None", "Pest", InstallBoost: false, InitializeGit: false
        )).SequenceEqual([
            LaravelProjectCreationStage.ValidatingRequest,
            LaravelProjectCreationStage.PreparingLaravelInstaller,
            LaravelProjectCreationStage.CreatingLaravelProject,
            LaravelProjectCreationStage.VerifyingProject,
            LaravelProjectCreationStage.RegisteringSite,
            LaravelProjectCreationStage.Completed
        ]),
        "Laravel project progress excludes unselected optional steps"
    );

    var boundedPhpSettings = PhpRuntimePolicy.Normalize(new PhpRuntimeSettings
    {
        MemoryLimitMegabytes = 1,
        MaxUploadMegabytes = 200_000,
        PhpCycle = " PHP 8.4 ",
        Debugger = new DebuggerSettings
        {
            Port = 90_000,
            IdeKey = " PHP STORM! "
        }
    });
    Check(boundedPhpSettings.MemoryLimitMegabytes == 16, "PHP memory settings enforce the supported minimum");
    Check(boundedPhpSettings.MaxUploadMegabytes == 100_000, "PHP upload settings enforce the supported maximum");
    Check(boundedPhpSettings.PhpCycle == "8.4", "PHP cycle settings discard unsupported characters");
    Check(boundedPhpSettings.Debugger.Port == 65_535, "Xdebug ports remain in the TCP range");
    Check(boundedPhpSettings.Debugger.IdeKey == "PHPSTORM", "Xdebug IDE keys discard unsafe characters");
    var phpOptions = PhpRuntimePolicy.BuildPhpOptions(boundedPhpSettings);
    Check(phpOptions["memory_limit"] == "16M", "PHP launch options use normalized memory settings");
    Check(phpOptions["upload_max_filesize"] == "100000M", "PHP launch options use normalized upload settings");

    var xdebugFixture = """
        {
          "draft": false,
          "prerelease": false,
          "tag_name": "3.5.3",
          "assets": [{
            "name": "php_xdebug-3.5.3-8.4-nts-vs17-x86_64.zip",
            "digest": "sha256:d2f40b2147767e12bdff26aa2b60757f7d375008c36d5703262e82a4e4105cfd",
            "browser_download_url": "https://github.com/xdebug/xdebug/releases/download/3.5.3/php_xdebug-3.5.3-8.4-nts-vs17-x86_64.zip"
          }]
        }
        """;
    var xdebugRelease = XdebugManager.SelectWindowsRelease(xdebugFixture, "8.4", "nts", "x86_64");
    Check(xdebugRelease.Version == "3.5.3", "Xdebug resolver selects the latest stable version");
    Check(
        xdebugRelease.FileName == "php_xdebug-3.5.3-8.4-nts-vs17-x86_64.zip"
            && xdebugRelease.DllName == "php_xdebug-3.5.3-8.4-nts-vs17-x86_64.dll",
        "Xdebug resolver selects the matching NTS x64 archive and DLL"
    );
    Check(
        xdebugRelease.Sha256 == "d2f40b2147767e12bdff26aa2b60757f7d375008c36d5703262e82a4e4105cfd",
        "Xdebug resolver preserves the published SHA-256"
    );
    Throws<InvalidOperationException>(
        () => XdebugManager.SelectWindowsRelease(xdebugFixture, "8.5", "nts", "x86_64"),
        "Xdebug resolver rejects a missing PHP build"
    );
    var xdebugArchive = Path.Combine(supportRoot, "xdebug-fixture.zip");
    var xdebugDll = Path.Combine(supportRoot, "xdebug-fixture.dll");
    using (var archive = ZipFile.Open(xdebugArchive, ZipArchiveMode.Create))
    {
        var expected = archive.CreateEntry(xdebugRelease.DllName);
        await using (var output = expected.Open()) await output.WriteAsync(new byte[] { 1, 2, 3, 4 });
        var ignored = archive.CreateEntry("../outside.dll");
        await using (var output = ignored.Open()) await output.WriteAsync(new byte[] { 9, 9, 9 });
    }
    await XdebugManager.ExtractDllAsync(xdebugRelease, xdebugArchive, xdebugDll, CancellationToken.None);
    Check(File.ReadAllBytes(xdebugDll).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Xdebug extraction selects only the expected DLL");
    Check(!File.Exists(Path.Combine(supportRoot, "outside.dll")), "Xdebug extraction ignores archive traversal entries");

    var currentNodeRow = new NodeRuntimeRow
    {
        Major = "24",
        InstalledVersion = "24.5.0",
        IsActive = true,
        IsUpdateAvailable = false
    };
    Check(!currentNodeRow.CanInstallOrUpdate, "current Node.js releases hide the update action");
    var missingNodeRow = new NodeRuntimeRow
    {
        Major = "22",
        InstalledVersion = null,
        IsActive = false,
        IsUpdateAvailable = false
    };
    Check(missingNodeRow.CanInstallOrUpdate, "missing Node.js releases expose the install action");

    var updateFeed = Path.Combine(supportRoot, "release-manifest.json");
    await File.WriteAllTextAsync(
        updateFeed,
        """
        {
          "releases": [
            { "version": "1.0.0", "build": 10, "channel": "stable", "notes": "Current", "downloadURLs": { "macOS": "https://example.test/current-macos", "windowsX64": "https://example.test/current-windows" } },
            { "version": "1.0.1", "build": 1, "channel": "beta", "notes": "Beta", "downloadURLs": { "macOS": "https://example.test/beta-macos", "windowsX64": "https://example.test/beta-windows" } },
            { "version": "1.0.0", "build": 11, "channel": "stable", "notes": "Build update", "downloadURLs": { "macOS": "https://example.test/build-macos", "windowsX64": "https://example.test/build-windows" } }
          ]
        }
        """
    );
    var updateManager = new AppUpdateManager(updateFeed, "1.0.0", 10);
    var stableUpdate = await updateManager.CheckAsync("Stable");
    Check(stableUpdate.AvailableRelease?.Build == 11, "equal versions compare release builds");
    var betaUpdate = await updateManager.CheckAsync("Beta");
    Check(betaUpdate.AvailableRelease?.Version == "1.0.1", "beta channel includes beta releases");
    Check(
        betaUpdate.AvailableRelease?.PlatformDownloadUrl == "https://example.test/beta-windows",
        "Windows update checks select the Windows x64 artifact"
    );
    var currentManager = new AppUpdateManager(updateFeed, "1.0.1", 1);
    Check(!(await currentManager.CheckAsync("Beta")).IsAvailable, "current beta release is up to date");

    var semanticUpdateFeed = Path.Combine(supportRoot, "semantic-release-manifest.json");
    await File.WriteAllTextAsync(
        semanticUpdateFeed,
        """
        {
          "releases": [
            { "version": "1.0.0-beta.10", "build": 99, "channel": "beta", "notes": "Prerelease", "downloadURLs": { "macOS": "https://example.test/prerelease-macos", "windowsX64": "https://example.test/prerelease-windows" } },
            { "version": "1.0.0", "build": 1, "channel": "stable", "notes": "Stable", "downloadURLs": { "macOS": "https://example.test/stable-macos", "windowsX64": "https://example.test/stable-windows" } }
          ]
        }
        """
    );
    var semanticUpdateManager = new AppUpdateManager(
        semanticUpdateFeed,
        "1.0.0-beta.9",
        100
    );
    Check(
        (await semanticUpdateManager.CheckAsync("Beta")).AvailableRelease?.Version == "1.0.0",
        "stable application releases outrank prereleases regardless of build number"
    );
    Check(
        RuntimeVersionComparison.Compare("1.0.0-beta.10", "1.0.0-beta.2") > 0,
        "application prerelease identifiers use semantic numeric precedence"
    );
    Check(
        RuntimeVersionComparison.Compare("1.0.0+macos", "1.0.0+windows") == 0,
        "application build metadata does not affect semantic precedence"
    );

    var signedUpdateFeed = Path.Combine(supportRoot, "signed-release-manifest.json");
    var updatePayload = await File.ReadAllBytesAsync(updateFeed);
    using var updateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var updateParameters = updateSigner.ExportParameters(false);
    var updatePublicKey = new byte[65];
    updatePublicKey[0] = 4;
    updateParameters.Q.X!.CopyTo(updatePublicKey, 1);
    updateParameters.Q.Y!.CopyTo(updatePublicKey, 33);
    var updateSignature = updateSigner.SignData(
        updatePayload,
        HashAlgorithmName.SHA256,
        DSASignatureFormat.Rfc3279DerSequence
    );
    await File.WriteAllTextAsync(
        signedUpdateFeed,
        JsonSerializer.Serialize(new
        {
            algorithm = "ECDSA_P256_SHA256",
            payload = Convert.ToBase64String(updatePayload),
            signature = Convert.ToBase64String(updateSignature)
        })
    );
    var signedUpdateManager = new AppUpdateManager(
        signedUpdateFeed,
        "1.0.0",
        10,
        Convert.ToBase64String(updatePublicKey)
    );
    Check(
        (await signedUpdateManager.CheckAsync("Stable")).AvailableRelease?.Build == 11,
        "signed update feeds verify with the bundled public key"
    );

    updateSignature[^1] ^= 1;
    await File.WriteAllTextAsync(
        signedUpdateFeed,
        JsonSerializer.Serialize(new
        {
            algorithm = "ECDSA_P256_SHA256",
            payload = Convert.ToBase64String(updatePayload),
            signature = Convert.ToBase64String(updateSignature)
        })
    );
    await ThrowsAsync<InvalidDataException>(
        async () => { _ = await signedUpdateManager.CheckAsync("Stable"); },
        "tampered update feeds are rejected"
    );

    var legacyUpdatePayload = Encoding.UTF8.GetBytes(
        """
        { "releases": [
          { "version": "1.0.1", "build": 2, "channel": "stable", "notes": "Legacy", "downloadURL": "https://example.test/herdme.zip" }
        ] }
        """
    );
    var legacyUpdateSignature = updateSigner.SignData(
        legacyUpdatePayload,
        HashAlgorithmName.SHA256,
        DSASignatureFormat.Rfc3279DerSequence
    );
    await File.WriteAllTextAsync(
        signedUpdateFeed,
        JsonSerializer.Serialize(new
        {
            algorithm = "ECDSA_P256_SHA256",
            payload = Convert.ToBase64String(legacyUpdatePayload),
            signature = Convert.ToBase64String(legacyUpdateSignature)
        })
    );
    await ThrowsAsync<InvalidDataException>(
        async () => { _ = await signedUpdateManager.CheckAsync("Stable"); },
        "signed feeds without both platform artifacts are rejected"
    );

    var sharedArtifactPayload = Encoding.UTF8.GetBytes(
        """
        { "releases": [
          { "version": "1.0.1", "build": 2, "channel": "stable", "notes": "Invalid shared artifact", "downloadURLs": { "macOS": "https://example.test/herdme.zip", "windowsX64": "https://example.test/herdme.zip" } }
        ] }
        """
    );
    var sharedArtifactSignature = updateSigner.SignData(
        sharedArtifactPayload,
        HashAlgorithmName.SHA256,
        DSASignatureFormat.Rfc3279DerSequence
    );
    await File.WriteAllTextAsync(
        signedUpdateFeed,
        JsonSerializer.Serialize(new
        {
            algorithm = "ECDSA_P256_SHA256",
            payload = Convert.ToBase64String(sharedArtifactPayload),
            signature = Convert.ToBase64String(sharedArtifactSignature)
        })
    );
    await ThrowsAsync<InvalidDataException>(
        async () => { _ = await signedUpdateManager.CheckAsync("Stable"); },
        "signed feeds cannot reuse one artifact for both platforms"
    );
    Check(RuntimeVersionComparison.IsNewer("v22.24.0", "22.23.1"), "runtime updates detect newer versions");
    Check(!RuntimeVersionComparison.IsNewer("5.31.0", "v5.31.0"), "current runtimes do not show updates");
    Check(
        !RuntimeVersionComparison.IsNewer("8.0.28", "8.0.29"),
        "a newer installed service runtime does not show a downgrade as an update"
    );
    Check(
        RuntimeVersionComparison.IsNewer(
            "1.0.0-beta.12-preview.1",
            "1.0.0-beta.11-preview.1"
        ),
        "RustFS prerelease updates compare their numeric identifiers"
    );
    Check(
        RuntimeVersionComparison.IsNewer(
            "RELEASE.2025-04-22T22-12-26Z",
            "RELEASE.2025-04-22T21-00-00Z"
        ),
        "MinIO timestamp releases compare chronologically"
    );
    Check(
        RuntimeVersionComparison.IsNewer("1.0.0", "1.0.0-beta.99"),
        "a stable semantic version is newer than its prerelease"
    );
    var missingPhpAction = RuntimeVersionComparison.InstallAction(false, null, "8.4.14");
    Check(missingPhpAction.IsVisible && missingPhpAction.Label == "Install", "missing PHP exposes install");
    var currentPhpAction = RuntimeVersionComparison.InstallAction(true, "8.4.14", "8.4.14");
    Check(!currentPhpAction.IsVisible, "current PHP hides the update action");
    var outdatedPhpAction = RuntimeVersionComparison.InstallAction(true, "8.4.13", "8.4.14");
    Check(
        outdatedPhpAction.IsVisible && outdatedPhpAction.Label == "Update",
        "outdated PHP exposes the update action"
    );
    Check(
        !RuntimeVersionComparison.InstallAction(true, null, "8.4.14").IsVisible,
        "unknown installed PHP versions do not show a false update"
    );

    var phpSupportRoot = Path.Combine(supportRoot, "php-installer");
    var phpRuntime = Path.Combine(phpSupportRoot, "Runtimes", "php", "8.4");
    Directory.CreateDirectory(phpRuntime);
    await File.WriteAllBytesAsync(Path.Combine(phpRuntime, "php.exe"), []);
    await File.WriteAllBytesAsync(Path.Combine(phpRuntime, "php-cgi.exe"), []);
    await File.WriteAllTextAsync(
        Path.Combine(phpRuntime, "herdme-runtime.json"),
        """
        { "runtime": "php", "cycle": "8.4", "version": "8.4.14" }
        """
    );
    var phpInspector = new PhpRuntimeInstaller(supportRoot: phpSupportRoot);
    Check(
        PhpRuntimeInstaller.SupportedCycles.SequenceEqual(
            new[] { "8.5", "8.4", "8.3", "8.2", "8.1", "8.0" }
        ),
        "PHP install policy matches the supported UI cycles"
    );
    Check(!PhpRuntimeInstaller.IsSupportedCycle("7.4"), "PHP 7.4 is not offered for new installs");
    await ThrowsAsync<ArgumentOutOfRangeException>(
        async () => { _ = await phpInspector.ResolveReleaseAsync("7.4"); },
        "unsupported PHP release checks fail before network access"
    );
    Check(phpInspector.IsInstalled("8.4"), "managed PHP requires both CLI and CGI executables");
    Check(phpInspector.InstalledVersion("8.4") == "8.4.14", "managed PHP reads its release manifest");
    var legacyPhpRuntime = Path.Combine(phpSupportRoot, "Runtimes", "php", "7.4");
    Directory.CreateDirectory(legacyPhpRuntime);
    await File.WriteAllBytesAsync(Path.Combine(legacyPhpRuntime, "php.exe"), []);
    await File.WriteAllBytesAsync(Path.Combine(legacyPhpRuntime, "php-cgi.exe"), []);
    Check(
        phpInspector.InstalledCycles().Contains("7.4", StringComparer.Ordinal),
        "installed legacy PHP remains visible and selectable"
    );
}
finally
{
    if (Directory.Exists(supportRoot)) Directory.Delete(supportRoot, true);
}

Console.WriteLine("HerdMe Windows cross-platform contract tests passed");

static string FindRepositoryRoot()
{
    foreach (var seed in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        for (var directory = new DirectoryInfo(seed); directory is not null; directory = directory.Parent)
        {
            if (
                File.Exists(Path.Combine(directory.FullName, "VERSION"))
                && File.Exists(Path.Combine(directory.FullName, "Windows", "installer.iss"))
            ) {
                return directory.FullName;
            }
        }
    }
    throw new DirectoryNotFoundException("The HerdMe repository root could not be located.");
}

static void VerifyReleaseAndInstallerContracts(string repositoryRoot)
{
    var version = File.ReadAllText(Path.Combine(repositoryRoot, "VERSION")).Trim();
    var versionParts = version.Split('.');
    Check(
        versionParts.Length == 3
            && versionParts.All(part =>
                uint.TryParse(part, out var value) && value <= ushort.MaxValue),
        "release version components fit Windows four-part file versions"
    );
    var buildText = File.ReadAllText(Path.Combine(repositoryRoot, "BUILD_NUMBER")).Trim();
    Check(
        uint.TryParse(buildText, out var buildNumber)
            && buildNumber is > 0 and <= ushort.MaxValue,
        "release build number fits Windows four-part file versions"
    );

    var installerPath = Path.Combine(repositoryRoot, "Windows", "installer.iss");
    var installerText = File.ReadAllText(installerPath);
    var setup = ParseInnoSection(installerText, "Setup");
    Check(
        setup.GetValueOrDefault("PrivilegesRequired") == "lowest"
            && setup.GetValueOrDefault("DefaultDirName") == @"{localappdata}\Programs\HerdMe",
        "the Windows installer remains per-user and does not require elevation"
    );
    Check(
        setup.GetValueOrDefault("ArchitecturesAllowed") == "x64compatible"
            && setup.GetValueOrDefault("ArchitecturesInstallIn64BitMode") == "x64compatible",
        "the Windows installer preserves its x64 architecture contract"
    );
    Check(
        setup.GetValueOrDefault("MinVersion") == "10.0.19041"
            && setup.GetValueOrDefault("AppMutex") == SingleInstanceCoordinator.MutexName,
        "the Windows installer matches the supported OS and runtime mutex"
    );
    Check(
        setup.GetValueOrDefault("CloseApplications") == "yes"
            && setup.GetValueOrDefault("CloseApplicationsFilter") == "HerdMe.Windows.exe"
            && setup.GetValueOrDefault("RestartApplications") == "no",
        "the Windows installer closes HerdMe safely without restarting it during upgrades"
    );
    Check(
        installerText.Contains(
            """Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs""",
            StringComparison.Ordinal
        ),
        "the Windows installer includes the complete validated portable payload"
    );
    Check(
        installerText.Contains(
            """ValueName: "HerdMe"; Flags: uninsdeletevalue""",
            StringComparison.Ordinal
        ),
        "the Windows uninstaller removes HerdMe's launch-at-login value"
    );
    var icon = setup.GetValueOrDefault("SetupIconFile");
    Check(
        !string.IsNullOrWhiteSpace(icon)
            && File.Exists(Path.Combine(repositoryRoot, "Windows", icon.Replace('\\', Path.DirectorySeparatorChar))),
        "the Windows installer icon exists at its declared path"
    );

    var portableScript = File.ReadAllText(
        Path.Combine(repositoryRoot, "Windows", "package-portable.ps1")
    );
    var portableCleanup = portableScript.IndexOf(
        """foreach ($oldOutput in @($archive, $checksumFile))""",
        StringComparison.Ordinal
    );
    var portableBuild = portableScript.IndexOf(
        """& (Join-Path $PSScriptRoot "build.ps1")""",
        StringComparison.Ordinal
    );
    Check(
        portableCleanup >= 0 && portableCleanup < portableBuild,
        "portable packaging removes stale ZIP and checksum candidates before building"
    );

    var setupScript = File.ReadAllText(
        Path.Combine(repositoryRoot, "Windows", "package-installer.ps1")
    );
    var setupCleanup = setupScript.IndexOf(
        """foreach ($oldOutput in @($installerPath, $checksumFile))""",
        StringComparison.Ordinal
    );
    var portableInvocation = setupScript.IndexOf(
        """& (Join-Path $PSScriptRoot "package-portable.ps1")""",
        StringComparison.Ordinal
    );
    Check(
        setupCleanup >= 0 && setupCleanup < portableInvocation,
        "Setup packaging removes stale installer and checksum candidates before building"
    );
}

static Dictionary<string, string> ParseInnoSection(string contents, string requestedSection)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var inSection = false;
    foreach (var rawLine in contents.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        var line = rawLine.Trim();
        if (line.StartsWith('[') && line.EndsWith(']'))
        {
            inSection = line[1..^1].Equals(requestedSection, StringComparison.OrdinalIgnoreCase);
            continue;
        }
        if (!inSection || line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
        var separator = line.IndexOf('=');
        if (separator <= 0) continue;
        result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
    }
    return result;
}

static async Task VerifyCoreClientAsync(string coreExecutable, string supportRoot)
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

    var contractExecutable = Environment.ProcessPath
        ?? throw new InvalidOperationException("The contract executable path is unavailable.");
    var extensions = await client.ValidatePhpAsync(contractExecutable);
    Check(
        extensions.Compatible && extensions.Missing.Count == 0,
        "CoreClient pipes PHP module output through the real core executable"
    );
}

static void Check(bool condition, string contract)
{
    if (!condition) throw new InvalidOperationException("Failed contract: " + contract);
}

static void Throws<TException>(Action action, string contract) where TException : Exception
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

static async Task ThrowsAsync<TException>(Func<Task> action, string contract) where TException : Exception
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

static async Task TestMailCaptureAsync(string supportRoot)
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

static async Task TestDumpCaptureAsync(string supportRoot)
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

static async Task TestFastCgiClientAsync()
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

static async Task TestLocalHttpSiteServerAsync(string supportRoot)
{
    var siteRoot = Path.Combine(supportRoot, "http-site");
    var publicRoot = Path.Combine(siteRoot, "public");
    Directory.CreateDirectory(publicRoot);
    await File.WriteAllTextAsync(Path.Combine(publicRoot, "index.html"), "HerdMe static site");
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
                $"mklink /J \"{externalLink}\" \"{externalRoot}\""
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

    var reparseEscape = await SendHttpRequestAsync(
        port,
        "GET /external-link/secret.txt HTTP/1.1\r\nHost: demo.local-test\r\nConnection: close\r\n\r\n"
    );
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
            "GET /stream.php HTTP/1.1\r\nHost: stream.local-test\r\nConnection: close\r\n\r\n"
        ));
        streamingClient.Client.Shutdown(SocketShutdown.Send);
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
}

static async Task TestCancelledCommandKillsTreeAsync(string supportRoot)
{
    var marker = Path.Combine(supportRoot, "cancelled-command-finished");
    string executable;
    string[] arguments;
    if (OperatingSystem.IsWindows())
    {
        executable = "cmd.exe";
        arguments = [
            "/d", "/s", "/c",
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

static async Task<string> SendHttpRequestAsync(int port, string request)
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

static async Task ReadHttpUntilAsync(
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

static async Task VerifyLiveServiceReleasesAsync()
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

static async Task VerifyLiveRuntimeReleasesAsync()
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
    foreach (var major in new[] { "26", "24", "22", "20" })
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

static async Task ProbeDownloadAsync(
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

static async Task HandleFastCgiFixtureAsync(
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

static async Task HandleStreamingFastCgiFixtureAsync(
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

static Dictionary<string, string> DecodeFastCgiParameters(byte[] content)
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

static int ReadFastCgiLength(byte[] content, ref int offset)
{
    if (offset >= content.Length) throw new InvalidDataException("Missing FastCGI fixture length.");
    if ((content[offset] & 0x80) == 0) return content[offset++];
    if (offset + 4 > content.Length) throw new InvalidDataException("Truncated FastCGI fixture length.");
    var length = BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(offset, 4)) & 0x7fff_ffff;
    offset += 4;
    return checked((int)length);
}

static async Task WriteFastCgiRecordAsync(Stream stream, byte type, byte[] content)
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

static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var count = await stream.ReadAsync(buffer[offset..]);
        if (count == 0) throw new EndOfStreamException("Fixture connection closed early.");
        offset += count;
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
