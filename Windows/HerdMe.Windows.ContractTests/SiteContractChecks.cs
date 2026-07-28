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
    internal static void VerifySiteContracts(string supportRoot)
    {
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

        var removalRoot = Path.Combine(supportRoot, "site-removal-root");
        var removableProject = Path.Combine(removalRoot, "removable-project");
        Directory.CreateDirectory(removableProject);
        var removableSite = new SiteRecord
        {
            Name = "Removable Project",
            Path = removableProject,
            Domain = "removable-project.test",
            Framework = "Laravel"
        };
        Check(
            SiteRemovalService.ResolveRemovableDirectory(removableSite, [removalRoot])
                == Path.TrimEndingDirectorySeparator(Path.GetFullPath(removableProject)),
            "site removal accepts a direct parked project"
        );
        Throws<SiteRemovalException>(
            () => SiteRemovalService.ResolveRemovableDirectory(
                new SiteRecord
                {
                    Name = removableSite.Name,
                    Path = removableSite.Path,
                    Domain = removableSite.Domain,
                    Framework = removableSite.Framework,
                    Linked = true
                },
                [removalRoot]
            ),
            "site removal rejects linked projects"
        );
        var outsideProject = Path.Combine(supportRoot, "outside-project");
        Directory.CreateDirectory(outsideProject);
        Throws<SiteRemovalException>(
            () => SiteRemovalService.ResolveRemovableDirectory(
                new SiteRecord { Name = "Outside", Path = outsideProject },
                [removalRoot]
            ),
            "site removal rejects projects outside configured park roots"
        );
        Check(
            !SiteRemovalService.IsRemovableDirectoryAttributes(
                FileAttributes.Directory | FileAttributes.ReparsePoint
            ),
            "site removal rejects symbolic links and directory junctions"
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
        site.GitSummary = "feature/login, 2 changes";
        Check(
            SitePresentation.Filter([site, other], "feature/login").SequenceEqual([site]),
            "site search includes the Git summary"
        );
        var dirtyGit = SitePresentation.ParseGitStatus(
            "## feature/login...origin/feature/login\n M app.cs\n?? notes.txt\n"
        );
        Check(
            dirtyGit.IsRepository && dirtyGit.Branch == "feature/login" && dirtyGit.ChangeCount == 2,
            "Git status parsing reports the branch and change count"
        );
        var freshGit = SitePresentation.ParseGitStatus("## No commits yet on main\n");
        Check(
            freshGit.Branch == "main" && freshGit.ChangeCount == 0,
            "Git status parsing handles repositories without commits"
        );
        var initialGit = SitePresentation.ParseGitStatus("## Initial commit on trunk\n");
        Check(initialGit.Branch == "trunk", "Git status parsing handles initial-commit output");
        var detachedGit = SitePresentation.ParseGitStatus("## HEAD (no branch)\n");
        Check(
            detachedGit.IsRepository && detachedGit.Branch is null && detachedGit.ChangeCount == 0,
            "Git status parsing handles detached HEAD"
        );
        var scanGeneration = new SiteScanGeneration();
        var firstGeneration = scanGeneration.Begin();
        Check(scanGeneration.IsCurrent(firstGeneration), "a new site scan generation is current");
        var secondGeneration = scanGeneration.Begin();
        Check(
            !scanGeneration.IsCurrent(firstGeneration)
                && scanGeneration.IsCurrent(secondGeneration),
            "a newer site scan rejects stale Git results"
        );
        scanGeneration.Invalidate();
        Check(
            !scanGeneration.IsCurrent(secondGeneration),
            "leaving the Sites page invalidates pending Git results"
        );
        using (var cancelledGitInspection = new CancellationTokenSource())
        {
            cancelledGitInspection.Cancel();
            Throws<OperationCanceledException>(
                () => SitePresentation.InspectGitStatusesAsync(
                    [site],
                    cancelledGitInspection.Token
                ).GetAwaiter().GetResult(),
                "site Git inspection honors cancellation"
            );
        }
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
    }
}
