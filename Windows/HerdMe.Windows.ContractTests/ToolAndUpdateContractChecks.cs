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
}

