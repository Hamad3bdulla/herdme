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
    internal static async Task VerifyToolAndUpdateContractsAsync(string supportRoot)
    {
        var gitDigest = new string('a', 64);
        var gitRelease = GitRuntimeInstaller.SelectRelease($$"""
            {
              "assets": [
                {
                  "name": "MinGit-2.55.0.3-busybox-64-bit.zip",
                  "size": 100,
                  "digest": "sha256:{{new string('b', 64)}}",
                  "browser_download_url": "https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.3/MinGit-2.55.0.3-busybox-64-bit.zip"
                },
                {
                  "name": "MinGit-2.55.0.3-64-bit.zip",
                  "size": 38791206,
                  "digest": "sha256:{{gitDigest}}",
                  "browser_download_url": "https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.3/MinGit-2.55.0.3-64-bit.zip"
                }
              ]
            }
            """);
        Check(
            gitRelease.Version == "2.55.0.3"
                && gitRelease.FileName == "MinGit-2.55.0.3-64-bit.zip"
                && gitRelease.Size == 38_791_206
                && gitRelease.Sha256 == gitDigest,
            "Git provisioning selects the official non-BusyBox MinGit x64 asset and SHA-256"
        );
        Throws<InvalidDataException>(
            () => GitRuntimeInstaller.SelectRelease(
                """{"assets":[{"name":"MinGit-2.55.0.3-64-bit.zip","size":1,"digest":null,"browser_download_url":"https://github.com/example.zip"}]}"""
            ),
            "Git provisioning rejects release assets without an official SHA-256 digest"
        );

        var installedGit = new GitRuntimeInstaller(supportRoot);
        var gitExecutable = installedGit.GitExecutable("2.55.0.3");
        Directory.CreateDirectory(Path.GetDirectoryName(gitExecutable)!);
        await File.WriteAllTextAsync(gitExecutable, "managed git");
        Check(
            installedGit.InstalledVersion() == "2.55.0.3"
                && await installedGit.EnsureInstalledAsync() == gitExecutable,
            "Git provisioning reuses an installed managed runtime without network access"
        );

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
        await LaravelProjectCreator.DeleteStagingDirectoryAsync(stagingFixture);
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
        foreach (var runtimeFile in PhpRuntimeInstaller.RequiredVcRuntimeFiles)
        {
            File.WriteAllText(
                Path.Combine(
                    Path.GetDirectoryName(installedPhp.PhpExecutable("8.4"))!,
                    runtimeFile
                ),
                "Microsoft VC143 fixture"
            );
        }
        File.WriteAllText(installedTools.ComposerPath, "managed composer");
        File.WriteAllText(installedTools.LaravelExecutable, "managed laravel installer");
        Check(
            installedTools.IsLaravelInstallerReady("8.4"),
            "Project creation detects the installed Laravel Installer without launching it"
        );
        await installedTools.EnsureLaravelInstallerAsync("8.4");
        Check(
            File.Exists(installedTools.ComposerCommandPath),
            "Project creation repairs the managed Composer command without downloading tools again"
        );
        var composerCommand = File.ReadAllText(installedTools.ComposerCommandPath);
        Check(
            composerCommand.Contains(
                @"%~dp0..\Runtimes\php\8.4\php.exe",
                StringComparison.Ordinal
            )
                && composerCommand.Contains(
                    @"%~dp0composer.phar",
                    StringComparison.Ordinal
                )
                && composerCommand.EndsWith("%*\r\n", StringComparison.Ordinal),
            "The managed Composer command uses HerdMe PHP and forwards Laravel Installer arguments"
        );
        var managedPath = installedTools.ManagedEnvironment("8.4")["PATH"]
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        Check(
            managedPath.Contains(
                Path.GetDirectoryName(installedTools.ComposerCommandPath)!,
                StringComparer.OrdinalIgnoreCase
            ),
            "Laravel Installer receives the managed Composer command directory on PATH"
        );
        Check(
            managedPath.Contains(
                Path.GetDirectoryName(gitExecutable)!,
                StringComparer.OrdinalIgnoreCase
            ),
            "Composer and Laravel child commands receive managed Git on PATH"
        );

        var managedBin = Path.GetDirectoryName(installedTools.ComposerCommandPath)!;
        var managedPhp = Path.GetDirectoryName(installedPhp.PhpExecutable("8.4"))!;
        var managedNode = Path.Combine(supportRoot, "Runtimes", "node", "22.99.0");
        var managedComposerBin = Path.Combine(installedTools.ComposerHome, "vendor", "bin");
        var systemTools = Path.GetFullPath(Path.Combine(supportRoot, "..", "system-tools"));
        var stalePhp = Path.Combine(supportRoot, "Runtimes", "php", "8.3");
        var mergedUserPath = WindowsUserPathManager.MergeManagedPath(
            string.Join(Path.PathSeparator, [systemTools, stalePhp, managedBin]),
            supportRoot,
            [managedBin, managedPhp, managedComposerBin, managedNode, Path.GetDirectoryName(gitExecutable)!]
        ).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        Check(
            mergedUserPath.Take(5).SequenceEqual(
                [managedBin, managedPhp, managedComposerBin, managedNode, Path.GetDirectoryName(gitExecutable)!],
                StringComparer.OrdinalIgnoreCase
            )
                && mergedUserPath.Contains(systemTools, StringComparer.OrdinalIgnoreCase)
                && !mergedUserPath.Contains(stalePhp, StringComparer.OrdinalIgnoreCase)
                && mergedUserPath.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    == mergedUserPath.Length,
            "User PATH puts active HerdMe tools first, preserves system entries, and removes stale HerdMe runtimes"
        );
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

        Check(ProductLinks.All.Count == 3, "About exposes all product links");
        Check(
            ProductLinks.All.Select(link => link.Uri).Distinct().Count() == ProductLinks.All.Count,
            "About product links are distinct"
        );
        Check(
            ProductLinks.All.All(link =>
                link.Uri.IsAbsoluteUri
                && link.Uri.Scheme == Uri.UriSchemeHttps
                && !string.IsNullOrWhiteSpace(link.Uri.Host)),
            "About product links use absolute HTTPS addresses"
        );

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
        var unpublishedReleaseHandler = new SequenceHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        );
        using (var unpublishedReleaseClient = new HttpClient(unpublishedReleaseHandler))
        {
            var unpublishedReleaseManager = new AppUpdateManager(
                "https://github.com/example/herdme/releases/latest/download/release-manifest.signed.json",
                "1.0.1",
                1,
                fallbackFeedLocation: updateFeed,
                httpClient: unpublishedReleaseClient
            );
            Check(
                !(await unpublishedReleaseManager.CheckAsync("Stable")).IsAvailable
                    && unpublishedReleaseHandler.CallCount == 1,
                "unpublished GitHub releases fall back to the bundled manifest without a 404 error"
            );
        }

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
        var vcRuntimeSource = Path.Combine(phpSupportRoot, "vc143-source");
        Directory.CreateDirectory(phpRuntime);
        Directory.CreateDirectory(vcRuntimeSource);
        foreach (var runtimeFile in PhpRuntimeInstaller.RequiredVcRuntimeFiles)
        {
            await File.WriteAllTextAsync(
                Path.Combine(vcRuntimeSource, runtimeFile),
                "Microsoft VC143 fixture: " + runtimeFile
            );
        }
        await File.WriteAllBytesAsync(Path.Combine(phpRuntime, "php.exe"), []);
        await File.WriteAllBytesAsync(Path.Combine(phpRuntime, "php-cgi.exe"), []);
        await File.WriteAllTextAsync(
            Path.Combine(phpRuntime, "herdme-runtime.json"),
            """
            { "runtime": "php", "cycle": "8.4", "version": "8.4.14" }
            """
        );
        var phpInspector = new PhpRuntimeInstaller(
            supportRoot: phpSupportRoot,
            vcRuntimeRoot: vcRuntimeSource
        );
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
        Check(
            !phpInspector.IsInstalled("8.4"),
            "managed PHP is incomplete without the app-local Visual C++ runtime"
        );
        PhpRuntimeInstaller.CopyVcRuntimeFiles(vcRuntimeSource, phpRuntime);
        Check(
            PhpRuntimeInstaller.RequiredVcRuntimeFiles.All(file =>
                File.ReadAllText(Path.Combine(phpRuntime, file))
                    == "Microsoft VC143 fixture: " + file
            ),
            "managed PHP receives every app-local Visual C++ runtime file"
        );
        Throws<InvalidDataException>(
            () => PhpRuntimeInstaller.CopyVcRuntimeFiles(
                Path.Combine(phpSupportRoot, "missing-vc143"),
                Path.Combine(phpSupportRoot, "invalid-runtime")
            ),
            "managed PHP rejects an incomplete app-local Visual C++ runtime"
        );
        var phpExtensionDirectory = Path.Combine(phpRuntime, "ext");
        Directory.CreateDirectory(phpExtensionDirectory);
        foreach (var extension in PhpRuntimeInstaller.RequiredManagedExtensions)
        {
            await File.WriteAllTextAsync(
                Path.Combine(phpExtensionDirectory, $"php_{extension}.dll"),
                "managed PHP extension fixture"
            );
        }
        Check(
            PhpRuntimeInstaller.RequiredManagedExtensions.Contains(
                "pdo_mysql",
                StringComparer.OrdinalIgnoreCase
            ) && PhpRuntimeInstaller.ManagedPhpIni.Contains(
                "extension = pdo_mysql",
                StringComparison.Ordinal
            ),
            "managed PHP enables Laravel's MySQL PDO driver"
        );
        var phpConfiguration = Path.Combine(phpRuntime, "php.ini");
        await File.WriteAllTextAsync(
            phpConfiguration,
            PhpRuntimeInstaller.ManagedPhpIni.Replace(
                "extension = zip",
                "; extension = zip",
                StringComparison.Ordinal
            )
        );
        Check(
            !PhpRuntimeInstaller.HasRequiredConfiguration(phpConfiguration),
            "managed PHP detects when Composer's ZIP extension is disabled"
        );
        phpInspector.EnsureManagedConfiguration("8.4");
        Check(
            PhpRuntimeInstaller.HasRequiredConfiguration(phpConfiguration),
            "managed PHP repairs every required Laravel and Composer extension"
        );
        var zipExtension = Path.Combine(phpExtensionDirectory, "php_zip.dll");
        File.Delete(zipExtension);
        Throws<InvalidDataException>(
            () => phpInspector.EnsureManagedConfiguration("8.4"),
            "managed PHP rejects a missing ZIP extension before Composer starts"
        );
        await File.WriteAllTextAsync(zipExtension, "managed PHP extension fixture");
        Check(
            phpInspector.IsInstalled("8.4"),
            "managed PHP requires CLI, CGI, and app-local VC++ executables"
        );
        Check(phpInspector.InstalledVersion("8.4") == "8.4.14", "managed PHP reads its release manifest");
        var legacyPhpRuntime = Path.Combine(phpSupportRoot, "Runtimes", "php", "7.4");
        Directory.CreateDirectory(legacyPhpRuntime);
        await File.WriteAllBytesAsync(Path.Combine(legacyPhpRuntime, "php.exe"), []);
        await File.WriteAllBytesAsync(Path.Combine(legacyPhpRuntime, "php-cgi.exe"), []);
        PhpRuntimeInstaller.CopyVcRuntimeFiles(vcRuntimeSource, legacyPhpRuntime);
        Check(
            phpInspector.InstalledCycles().Contains("7.4", StringComparer.Ordinal),
            "installed legacy PHP remains visible and selectable"
        );
    }
}
