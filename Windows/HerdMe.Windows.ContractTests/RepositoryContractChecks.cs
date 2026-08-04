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
    internal static string FindRepositoryRoot()
    {
        foreach (var seed in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(seed); directory is not null; directory = directory.Parent)
            {
                if (
                    File.Exists(Path.Combine(directory.FullName, "VERSION"))
                    && File.Exists(Path.Combine(directory.FullName, "Windows", "installer.iss"))
                )
                {
                    return directory.FullName;
                }
            }
        }
        throw new DirectoryNotFoundException("The HerdMe repository root could not be located.");
    }

    internal static void VerifyRuntimeCatalogContracts(string repositoryRoot)
    {
        Check(RuntimeCatalog.LoadIssue is null, "the shared runtime catalog loads from the assembly");
        Check(RuntimeCatalog.DefaultPhpCycle == "8.4", "the shared catalog defines default PHP 8.4");
        Check(RuntimeCatalog.DefaultNodeMajor == "22", "the shared catalog defines default Node.js 22");
        Check(
            RuntimeCatalog.InstallablePhpCycles.SequenceEqual(
                new[] { "8.5", "8.4", "8.3", "8.2", "8.1", "8.0" }
            ),
            "the shared catalog defines supported PHP cycles"
        );
        Check(
            RuntimeCatalog.WindowsNodeMajors.SequenceEqual(new[] { "26", "24", "22", "20" }),
            "the shared catalog defines supported Windows Node.js majors"
        );
        Check(
            RuntimeCatalog.Services.Select(service => service.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals([
                    "mariadb",
                    "mysql",
                    "postgresql",
                    "mongodb",
                    "redis",
                    "valkey",
                    "meilisearch",
                    "typesense",
                    "minio",
                    "rustfs"
                ]),
            "the shared catalog defines the complete service set"
        );
        Check(
            RuntimeCatalog.Services.Single(service => service.Id == "rustfs")
                .MacOS.Architectures.SequenceEqual(new[] { "arm64" }),
            "the shared catalog restricts macOS RustFS to arm64"
        );

        var catalogPath = Path.Combine(
            repositoryRoot,
            "HerdMe",
            "Resources",
            "runtime-catalog.json"
        );
        var invalidCatalog = File.ReadAllText(catalogPath).Replace(
            "\"schemaVersion\": 1",
            "\"schemaVersion\": 2",
            StringComparison.Ordinal
        );
        Throws<InvalidDataException>(
            () => RuntimeCatalog.ValidateData(Encoding.UTF8.GetBytes(invalidCatalog)),
            "the shared catalog rejects unsupported schema versions"
        );
    }

    internal static void VerifyCompositionContracts(string repositoryRoot)
    {
        Check(
            typeof(AppServices).IsSealed && !typeof(AppServices).IsAbstract,
            "the Windows composition root is an application-owned instance"
        );

        var services = new AppServices();
        Check(
            ReferenceEquals(
                PrivateDependency<CoreClient>(services.ComposerTools, "coreClient"),
                services.Core
            )
                && ReferenceEquals(
                    PrivateDependency<PhpRuntimeInstaller>(services.ComposerTools, "phpInstaller"),
                    services.PhpInstaller
                )
                && ReferenceEquals(
                    PrivateDependency<PhpRuntimePolicy>(services.ComposerTools, "phpPolicy"),
                    services.RuntimePolicy
                )
                && ReferenceEquals(
                    PrivateDependency<NodeRuntimeInstaller>(services.ComposerTools, "nodeInstaller"),
                    services.NodeInstaller
                ),
            "Composer reuses the application-owned Core, PHP, and Node services"
        );
        Check(
            ReferenceEquals(
                PrivateDependency<ComposerToolManager>(services.ProjectCreator, "tools"),
                services.ComposerTools
            )
                && ReferenceEquals(
                    PrivateDependency<PhpRuntimeInstaller>(services.ProjectCreator, "phpInstaller"),
                    services.PhpInstaller
                )
                && ReferenceEquals(
                    PrivateDependency<PhpRuntimePolicy>(services.ProjectCreator, "phpPolicy"),
                    services.RuntimePolicy
                )
                && ReferenceEquals(
                    PrivateDependency<NodeRuntimeInstaller>(services.ProjectCreator, "nodeInstaller"),
                    services.NodeInstaller
                )
                && ReferenceEquals(
                    PrivateDependency<GitRuntimeInstaller>(services.ProjectCreator, "gitInstaller"),
                    services.GitInstaller
                )
                && ReferenceEquals(
                    PrivateDependency<WindowsUserPathManager>(services.ProjectCreator, "userPathManager"),
                    services.UserPath
                ),
            "Laravel project creation reuses the application-owned runtime, Git, and PATH services"
        );
        Check(
            ReferenceEquals(
                PrivateDependency<CoreClient>(services.Environment, "coreClient"),
                services.Core
            )
                && ReferenceEquals(
                    PrivateDependency<PhpRuntimeInstaller>(services.Environment, "runtimeInstaller"),
                    services.PhpInstaller
                )
                && ReferenceEquals(
                    PrivateDependency<PhpRuntimePolicy>(services.Environment, "runtimePolicy"),
                    services.RuntimePolicy
                )
                && ReferenceEquals(
                    PrivateDependency<WindowsCertificateManager>(services.Environment, "certificateManager"),
                    services.Certificates
                )
                && ReferenceEquals(
                    PrivateDependency<WindowsHostsManager>(services.Environment, "hostsManager"),
                    services.Hosts
                ),
            "the Windows web environment reuses the application-owned runtime and trust services"
        );
        Check(
            ReferenceEquals(
                PrivateDependency<SiteConfigurationStore>(services.InitialSetup, "settingsStore"),
                services.SiteSettings
            )
                && ReferenceEquals(
                    PrivateDependency<ComposerToolManager>(services.InitialSetup, "composerTools"),
                    services.ComposerTools
                )
                && ReferenceEquals(
                    PrivateDependency<NodeRuntimeInstaller>(services.InitialSetup, "nodeInstaller"),
                    services.NodeInstaller
                )
                && ReferenceEquals(
                    PrivateDependency<GitRuntimeInstaller>(services.InitialSetup, "gitInstaller"),
                    services.GitInstaller
                )
                && ReferenceEquals(
                    PrivateDependency<WindowsUserPathManager>(services.InitialSetup, "userPathManager"),
                    services.UserPath
                ),
            "first-run setup reuses the application-owned settings, runtime, Git, and PATH services"
        );

        var windowsRoot = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        var pageSources = Directory.EnumerateFiles(
                Path.Combine(windowsRoot, "Pages"),
                "*Page.xaml.cs"
            )
            .Select(File.ReadAllText)
            .ToArray();
        Check(
            pageSources.All(source => !source.Contains("AppServices.", StringComparison.Ordinal)),
            "Windows pages do not use a static service locator"
        );
        Check(
            pageSources.All(source => !Regex.IsMatch(
                source,
                @"new\s+(CoreClient|SiteConfigurationStore|PhpRuntimeInstaller|PhpRuntimePolicy|ComposerToolManager|NodeRuntimeInstaller|GitRuntimeInstaller|WindowsUserPathManager|WindowsLocalEnvironment|WindowsServiceManager)\s*\("
            )),
            "Windows pages do not construct duplicate application services"
        );

        var mainWindowSource = File.ReadAllText(Path.Combine(windowsRoot, "MainWindow.xaml.cs"));
        Check(
            mainWindowSource.Contains("public MainWindow(", StringComparison.Ordinal)
                && mainWindowSource.Contains("AppServices services", StringComparison.Ordinal)
                && mainWindowSource.Contains("if (ContentFrame.Content is null) ShowPage(\"dashboard\")", StringComparison.Ordinal)
                && !mainWindowSource.Contains("ContentFrame.Navigate", StringComparison.Ordinal),
            "the main window injects dependencies and always creates its initial dashboard"
        );
        var onboardingSource = File.ReadAllText(
            Path.Combine(windowsRoot, "Views", "OnboardingView.xaml.cs")
        );
        Check(
            onboardingSource.Contains("Configure(InitialSetupManager setupManager)", StringComparison.Ordinal)
                && !onboardingSource.Contains("new InitialSetupManager", StringComparison.Ordinal),
            "first-run onboarding receives the shared setup manager"
        );
    }

    internal static void VerifySiteWorkflowContracts(string repositoryRoot)
    {
        var projectRoot = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        var xaml = File.ReadAllText(Path.Combine(projectRoot, "Pages", "SitesPage.xaml"));
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "Pages",
            "SitesPage.Workflows.cs"
        ));
        Check(
            xaml.Contains("x:Uid=\"SitesAutomationResetProject\"", StringComparison.Ordinal)
                && xaml.Contains("Click=\"ResetProjectWorkflow_Click\"", StringComparison.Ordinal),
            "the Sites automation menu exposes the local project reset workflow"
        );
        var pageSource = File.ReadAllText(Path.Combine(
            projectRoot,
            "Pages",
            "SitesPage.xaml.cs"
        ));
        Check(
            xaml.Contains("x:Name=\"SiteOperationLogText\"", StringComparison.Ordinal)
                && xaml.Contains("TextWrapping=\"NoWrap\"", StringComparison.Ordinal)
                && pageSource.Contains("AppendSiteOperationOutput(text)", StringComparison.Ordinal)
                && !pageSource.Contains(
                    "(SiteOperationBar.Message + text).Trim()",
                    StringComparison.Ordinal
                ),
            "site workflow output retains command line breaks in a bounded scrollable log"
        );

        var resetStart = source.IndexOf(
            "private async void ResetProjectWorkflow_Click",
            StringComparison.Ordinal
        );
        var resetEnd = source.IndexOf(
            "private async void ExportProjectWorkflow_Click",
            Math.Max(resetStart, 0),
            StringComparison.Ordinal
        );
        Check(
            resetStart >= 0 && resetEnd > resetStart,
            "the local project reset workflow has a bounded implementation"
        );
        var resetSource = source[resetStart..resetEnd];
        var backup = resetSource.IndexOf("CreateWorkflowBackupAsync", StringComparison.Ordinal);
        var destructiveReset = resetSource.IndexOf(
            "[\"migrate:fresh\", \"--seed\", \"--force\", \"--no-interaction\"]",
            StringComparison.Ordinal
        );
        Check(
            backup >= 0 && destructiveReset > backup,
            "the local project reset creates a recovery backup before rebuilding the database"
        );
        Check(
            resetSource.Contains("HasSupportedLocalResetDatabase", StringComparison.Ordinal)
                && resetSource.Contains("EnsureResetDatabaseMatchesBackup", StringComparison.Ordinal)
                && resetSource.Contains("if (databaseResetStarted)", StringComparison.Ordinal)
                && source.Contains("ResolveSqliteDatabasePath", StringComparison.Ordinal),
            "the local project reset permits only recognized local databases and rechecks the target after backup"
        );
        Check(
            resetSource.Contains("TryRestoreResetDatabaseAsync", StringComparison.Ordinal)
                && source.Contains(
                    "[\"db:wipe\", \"--force\", \"--no-interaction\"]",
                    StringComparison.Ordinal
                )
                && source.Contains("RestoreSiteDatabaseAsync", StringComparison.Ordinal),
            "the local project reset attempts a clean database restore after failure"
        );
        Check(
            resetSource.Contains("ClearLaravelGeneratedFiles", StringComparison.Ordinal)
                && resetSource.Contains("ClearLaravelLogs", StringComparison.Ordinal),
            "the successful local project reset clears Laravel caches, sessions, views, and logs"
        );
    }

    internal static T PrivateDependency<T>(object owner, string fieldName) where T : class
    {
        return owner.GetType().GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            )?.GetValue(owner) as T
            ?? throw new InvalidOperationException(
                $"{owner.GetType().Name}.{fieldName} is not the expected dependency."
            );
    }

    internal static void VerifyReleaseAndInstallerContracts(string repositoryRoot)
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
        var coreCmake = File.ReadAllText(Path.Combine(repositoryRoot, "Core", "CMakeLists.txt"));
        Check(
            coreCmake.Contains(
                "CMAKE_MSVC_RUNTIME_LIBRARY \"MultiThreaded$<$<CONFIG:Debug>:Debug>\"",
                StringComparison.Ordinal
            ),
            "the Windows core statically links the MSVC runtime for clean machines"
        );
        Check(
            coreCmake.Contains("if(MINGW)", StringComparison.Ordinal)
                && coreCmake.Contains(
                    "target_link_options(herdme-core PRIVATE -static)",
                    StringComparison.Ordinal
                ),
            "the MinGW Windows core does not require external C++ runtime DLLs"
        );
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
            setup.GetValueOrDefault("WizardResizable") == "no"
                && setup.GetValueOrDefault("WizardSizePercent") == "100",
            "the Windows installer keeps a fixed wizard size"
        );
        Check(
            installerText.Contains(
                """Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs""",
                StringComparison.Ordinal
            ),
            "the Windows installer includes the complete validated portable payload"
        );
        Check(
            installerText.Contains("CurUninstallStep <> usPostUninstall", StringComparison.Ordinal)
                && installerText.Contains(
                    "{localappdata}\\HerdMe\\Config\\onboarding-after-reinstall.flag",
                    StringComparison.Ordinal
                )
                && installerText.Contains("SaveStringToFile", StringComparison.Ordinal),
            "a completed uninstall requests onboarding after reinstall without deleting user data"
        );
        var startupRegistryLine = installerText.Split('\n').SingleOrDefault(line =>
            line.Contains("ValueName: \"HerdMe\"", StringComparison.Ordinal)
        );
        Check(
            startupRegistryLine is not null
                && startupRegistryLine.Contains("ValueType: none", StringComparison.Ordinal)
                && startupRegistryLine.Contains(
                    "Flags: dontcreatekey uninsdeletevalue",
                    StringComparison.Ordinal
                )
                && !startupRegistryLine.Contains("ValueData:", StringComparison.Ordinal),
            "the app owns launch-at-login while the Windows uninstaller only removes its value"
        );
        var startupExecutable = @"C:\Program Files\HerdMe\HerdMe.Windows.exe";
        var startupCommand = WindowsStartupCommand.Create(startupExecutable);
        Check(
            startupCommand == "\"C:\\Program Files\\HerdMe\\HerdMe.Windows.exe\" --background"
                && WindowsStartupCommand.Matches(startupCommand.ToUpperInvariant(), startupExecutable)
                && !WindowsStartupCommand.Matches(
                    "\"C:\\Program Files\\HerdMe\\HerdMe.Windows.exe\"",
                    startupExecutable
                ),
            "Windows launch-at-login uses the current quoted executable in hidden background mode"
        );
        Throws<ArgumentException>(
            () => WindowsStartupCommand.Create("C:\\Invalid\"Path\\HerdMe.Windows.exe"),
            "Windows launch-at-login rejects unsafe executable paths"
        );
        if (OperatingSystem.IsWindows())
        {
            var contractExecutable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The contract executable path is unavailable.");
            var shortcutRoot = Path.Combine(
                Path.GetTempPath(),
                $"herdme-startup-contract-{Guid.NewGuid():N}"
            );
            var shortcutPath = Path.Combine(shortcutRoot, "HerdMe.lnk");
            WindowsStartupShortcut.Create(shortcutPath, contractExecutable);
            Check(
                WindowsStartupShortcut.Matches(shortcutPath, contractExecutable),
                "Windows launch-at-login creates a valid background startup shortcut"
            );
            WindowsStartupShortcut.Delete(shortcutPath);
            Check(!File.Exists(shortcutPath), "Windows launch-at-login removes its startup shortcut");
            Directory.Delete(shortcutRoot);
        }
        Check(
            installerText.Contains(
                "Type: files; Name: \"{userstartup}\\HerdMe.lnk\"",
                StringComparison.Ordinal
            ),
            "the Windows uninstaller removes the launch-at-login shortcut"
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
        Check(
            portableScript.Contains("\"HerdMe.Windows.pri\"", StringComparison.Ordinal)
                && portableScript.Contains(
                    "\"Microsoft.Windows.ApplicationModel.Resources.dll\"",
                    StringComparison.Ordinal
                )
                && portableScript.Contains("\"MRM.dll\"", StringComparison.Ordinal),
            "portable packaging requires the unpackaged MRT Core resource payload"
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
        var signingScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Windows", "sign-windows-artifact.ps1")
        );
        var emptyThumbprintGuard = signingScript.IndexOf(
            "[string]::IsNullOrWhiteSpace($configuredThumbprint)",
            StringComparison.Ordinal
        );
        var thumbprintNormalization = signingScript.IndexOf(
            ".ToUpperInvariant()",
            StringComparison.Ordinal
        );
        Check(
            emptyThumbprintGuard >= 0
                && thumbprintNormalization >= 0
                && emptyThumbprintGuard < thumbprintNormalization
                && signingScript.Contains("TimeStamperCertificate", StringComparison.Ordinal),
            "Windows signing rejects missing identity configuration and requires a trusted timestamp"
        );

        var appSource = File.ReadAllText(
            Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows", "App.xaml.cs")
        );
        var mainWindowSource = File.ReadAllText(
            Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows", "MainWindow.xaml.cs")
        );
        var dashboardXaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Windows",
            "HerdMe.Windows",
            "Pages",
            "DashboardPage.xaml"
        ));
        var dashboardSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Windows",
            "HerdMe.Windows",
            "Pages",
            "DashboardPage.xaml.cs"
        ));
        Check(
            mainWindowSource.Contains("GetDpiForWindow", StringComparison.Ordinal)
                && mainWindowSource.Contains("LogicalWindowWidth * scale", StringComparison.Ordinal)
                && mainWindowSource.Contains("displayArea.WorkArea", StringComparison.Ordinal)
                && mainWindowSource.Contains("MoveAndResize", StringComparison.Ordinal)
                && mainWindowSource.Contains("IsResizable = false", StringComparison.Ordinal)
                && mainWindowSource.Contains("IsMaximizable = false", StringComparison.Ordinal)
                && mainWindowSource.Contains("IsMinimizable = false", StringComparison.Ordinal),
            "the main window keeps a fixed DPI-aware size within the display work area"
        );
        Check(
            dashboardXaml.Contains("SizeChanged=\"Page_SizeChanged\"", StringComparison.Ordinal)
                && dashboardXaml.Contains("x:Name=\"SummaryCardsGrid\"", StringComparison.Ordinal)
                && dashboardXaml.Contains("x:Key=\"DashboardSummaryCardStyle\"", StringComparison.Ordinal)
                && dashboardXaml.Contains("Property=\"HorizontalAlignment\" Value=\"Stretch\"", StringComparison.Ordinal)
                && dashboardXaml.Contains("TextWrapping=\"Wrap\"", StringComparison.Ordinal)
                && dashboardSource.Contains("e.NewSize.Width < 680", StringComparison.Ordinal)
                && dashboardSource.Contains("SummaryStatusBrush", StringComparison.Ordinal)
                && dashboardSource.Contains("PositionEnvironmentRow", StringComparison.Ordinal)
                && dashboardSource.Contains("RecentDumpsPanel", StringComparison.Ordinal),
            "the dashboard keeps polished equal-width cards and switches to a compact layout without clipping"
        );
        var environmentSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Windows",
            "HerdMe.Windows",
            "Services",
            "WindowsLocalEnvironment.cs"
        ));
        var serviceSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Windows",
            "HerdMe.Windows",
            "Services",
            "WindowsServiceManager.cs"
        ));
        Check(
            !appSource.Contains("environment.log", StringComparison.Ordinal)
                && !appSource.Contains("startup.log", StringComparison.Ordinal)
                && !appSource.Contains("unhandled.log", StringComparison.Ordinal)
                && appSource.Contains(
                    "WriteEnvironmentStartupFailureAsync",
                    StringComparison.Ordinal
                )
                && appSource.Contains(
                    "WriteBackgroundServiceStartupFailureAsync",
                    StringComparison.Ordinal
                )
                && appSource.Contains("WriteUnhandledExceptionAsync", StringComparison.Ordinal)
                && appSource.Contains(
                    "WriteAutomaticUpdateCheckFailureAsync",
                    StringComparison.Ordinal
                )
                && appSource.Contains(
                    "WriteManagedComponentUpdateCheckFailureAsync",
                    StringComparison.Ordinal
                ),
            "Windows application failures are routed exclusively through structured diagnostics"
        );
        Check(
            appSource.Contains("MainWindow.Activated += MainWindow_Activated", StringComparison.Ordinal)
                && appSource.Contains("settings.AutomaticUpdates", StringComparison.Ordinal)
                && appSource.Contains(
                    "Interlocked.Exchange(ref automaticUpdateCheckStarted, 1)",
                    StringComparison.Ordinal
                )
                && appSource.Contains("services.Updates.CheckAsync(channel)", StringComparison.Ordinal)
                && appSource.Contains("AppUpdatePrompt.ShowAsync", StringComparison.Ordinal)
                && appSource.Contains(
                    "services.ComponentUpdates.CheckAsync()",
                    StringComparison.Ordinal
                )
                && appSource.Contains(
                    "ManagedComponentUpdatePrompt.ShowAsync",
                    StringComparison.Ordinal
                )
                && appSource.Contains(
                    "CheckForUpdatesInBackgroundAsync",
                    StringComparison.Ordinal
                )
                && appSource.Contains("result.UsedBundledFallback", StringComparison.Ordinal),
            "Windows checks once in the background for application and component updates and prompts from live results"
        );
        Check(
            environmentSource.Contains(
                "WriteEnvironmentRecoveryFailureAsync",
                StringComparison.Ordinal
            )
                && serviceSource.Contains(
                    "WriteManagedServiceStartupFailureAsync",
                    StringComparison.Ordinal
                ),
            "Windows recovery and managed-service failures are routed through structured diagnostics"
        );
    }

    internal static Dictionary<string, string> ParseInnoSection(string contents, string requestedSection)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;
        foreach (var rawLine in contents.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
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

    internal static void VerifyXamlContracts(string repositoryRoot)
    {
        var projectRoot = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        var xamlFiles = Directory.EnumerateFiles(
            projectRoot,
            "*.xaml",
            SearchOption.AllDirectories
        ).Where(xamlPath =>
            !Path.GetRelativePath(projectRoot, xamlPath)
                .Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries
                )
                .Any(segment => segment is "bin" or "obj")
        ).Order(StringComparer.Ordinal).ToArray();
        Check(xamlFiles.Length >= 13, "the native Windows project includes every expected XAML surface");

        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var xamlPath in xamlFiles)
        {
            using var stream = File.OpenRead(xamlPath);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            var root = document.Root;
            Check(
                root is not null,
                $"{Path.GetRelativePath(repositoryRoot, xamlPath)} has a root element"
            );
            var documentRoot = root!;

            var xClass = documentRoot.Attribute(xamlNamespace + "Class")?.Value.Trim()
                ?? string.Empty;
            Check(
                !string.IsNullOrWhiteSpace(xClass),
                $"{Path.GetRelativePath(repositoryRoot, xamlPath)} declares x:Class"
            );
            var classSeparator = xClass.LastIndexOf('.');
            Check(
                classSeparator > 0 && classSeparator < xClass.Length - 1,
                $"{Path.GetRelativePath(repositoryRoot, xamlPath)} declares a qualified x:Class"
            );

            var codeBehindPath = xamlPath + ".cs";
            Check(
                File.Exists(codeBehindPath),
                $"{Path.GetRelativePath(repositoryRoot, xamlPath)} has code-behind"
            );
            var codeBehind = File.ReadAllText(codeBehindPath);
            var partialClassSource = string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(
                    Path.GetDirectoryName(xamlPath)!,
                    Path.GetFileNameWithoutExtension(xamlPath) + "*.cs",
                    SearchOption.TopDirectoryOnly
                ).Select(File.ReadAllText)
            );
            var namespaceName = xClass[..classSeparator];
            var className = xClass[(classSeparator + 1)..];
            Check(
                Regex.IsMatch(
                    codeBehind,
                    @"\bnamespace\s+" + Regex.Escape(namespaceName) + @"\s*[;{]",
                    RegexOptions.CultureInvariant
                ) && Regex.IsMatch(
                    codeBehind,
                    @"\bpartial\s+class\s+" + Regex.Escape(className) + @"\b",
                    RegexOptions.CultureInvariant
                ),
                $"{Path.GetRelativePath(repositoryRoot, codeBehindPath)} matches {xClass}"
            );

            var namedElements = documentRoot.DescendantsAndSelf()
                .SelectMany(element => element.Attributes(xamlNamespace + "Name"))
                .Select(attribute => attribute.Value)
                .ToArray();
            var duplicateName = namedElements.GroupBy(name => name, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1)?.Key;
            Check(
                duplicateName is null,
                $"{Path.GetRelativePath(repositoryRoot, xamlPath)} has unique x:Name values"
            );

            var symbolIcons = documentRoot.DescendantsAndSelf()
                .Where(element => element.Name.LocalName == "SymbolIcon")
                .ToArray();
            Check(
                symbolIcons.All(element => element.Attribute("FontSize") is null),
                $"{Path.GetRelativePath(repositoryRoot, xamlPath)} sizes icons with FontIcon instead of unsupported SymbolIcon.FontSize"
            );
            Check(
                symbolIcons.All(element =>
                    !string.Equals(
                        element.Attribute("Symbol")?.Value,
                        "Code",
                        StringComparison.Ordinal
                    )
                ),
                $"{Path.GetRelativePath(repositoryRoot, xamlPath)} uses only supported Symbol enum values for code icons"
            );

            var handlers = documentRoot.DescendantsAndSelf()
                .SelectMany(element => element.Attributes())
                .Where(attribute =>
                    attribute.Name.NamespaceName.Length == 0
                    && Regex.IsMatch(
                        attribute.Value,
                        @"^[A-Za-z_][A-Za-z0-9_]*_[A-Za-z][A-Za-z0-9_]*$",
                        RegexOptions.CultureInvariant
                    ))
                .Select(attribute => attribute.Value)
                .Distinct(StringComparer.Ordinal);
            foreach (var handler in handlers)
            {
                Check(
                    Regex.IsMatch(
                        partialClassSource,
                        @"\b(?:async\s+)?void\s+" + Regex.Escape(handler) + @"\s*\(",
                        RegexOptions.CultureInvariant
                    ),
                    $"{Path.GetRelativePath(repositoryRoot, xamlPath)} handler {handler} exists in code-behind"
                );
            }
        }

        var appDocument = XDocument.Load(Path.Combine(projectRoot, "App.xaml"));
        Check(
            appDocument.Descendants().All(element => element.Name.LocalName != "TaskbarIcon"),
            "the third-party tray icon is kept out of the compiled XAML surface"
        );
        var appCodeBehind = File.ReadAllText(Path.Combine(projectRoot, "App.xaml.cs"));
        Check(
            appCodeBehind.Contains("new TaskbarIcon", StringComparison.Ordinal)
                && appCodeBehind.Contains("new BitmapImage", StringComparison.Ordinal)
                && appCodeBehind.Contains(
                    "ms-appx:///Assets/HerdMe.ico",
                    StringComparison.Ordinal
                )
                && appCodeBehind.Contains(
                    "ContextMenuMode = ContextMenuMode.PopupMenu",
                    StringComparison.Ordinal
                )
                && !appCodeBehind.Contains("ContextMenuMode.SecondWindow", StringComparison.Ordinal)
                && !appCodeBehind.Contains("new GeneratedIconSource", StringComparison.Ordinal),
            "the Windows tray uses the packaged icon and a work-area-aware popup menu"
        );
        Check(
            !appCodeBehind.Contains("Resources[\"OpenHerdMeCommand\"]", StringComparison.Ordinal)
                && !appCodeBehind.Contains("Resources[\"QuitHerdMeCommand\"]", StringComparison.Ordinal)
                && !appCodeBehind.Contains("Resources[\"StartAllCommand\"]", StringComparison.Ordinal)
                && !appCodeBehind.Contains("Resources[\"StopAllCommand\"]", StringComparison.Ordinal)
                && appCodeBehind.Contains("new XamlUICommand", StringComparison.Ordinal)
                && appCodeBehind.Contains(
                    "AppLocalization.Get(\"TrayOpenCommandLabel\")",
                    StringComparison.Ordinal
                )
                && appCodeBehind.Contains(
                    "AppLocalization.Get(\"TrayQuitCommandLabel\")",
                    StringComparison.Ordinal
                )
                && appCodeBehind.Contains(
                    "AppLocalization.Get(\"TrayStartAllCommandLabel\")",
                    StringComparison.Ordinal
                )
                && appCodeBehind.Contains(
                    "AppLocalization.Get(\"TrayStopAllCommandLabel\")",
                    StringComparison.Ordinal
                ),
            "the Windows tray commands use direct ResourceLoader keys at runtime"
        );

        var projectDocument = XDocument.Load(
            Path.Combine(projectRoot, "HerdMe.Windows.csproj")
        );
        Check(
            projectDocument.Descendants().Any(element =>
                element.Name.LocalName == "WindowsSdkPackageVersion"
                && element.Value == "10.0.19041.56"
            ),
            "the Windows project pins the Windows SDK reference required by its App SDK version"
        );
        Check(
            projectDocument.Descendants()
                .Any(element =>
                    element.Name.LocalName == "Content"
                    && element.Attribute("Include")?.Value == @"Assets\HerdMe.ico"
                    && element.Elements().Any(child =>
                        child.Name.LocalName == "CopyToOutputDirectory"
                        && child.Value == "PreserveNewest"
                    )
                    && element.Elements().Any(child =>
                        child.Name.LocalName == "CopyToPublishDirectory"
                        && child.Value == "PreserveNewest"
                    )
                ),
            "the Windows tray icon is copied to build and publish outputs"
        );
        Check(
            projectDocument.Descendants()
                .Any(element =>
                    element.Name.LocalName == "Content"
                    && element.Attribute("Include")?.Value
                        == @"$(HerdMeVCRuntimeDirectory)\*.dll"
                    && element.Elements().Any(child =>
                        child.Name.LocalName == "CopyToOutputDirectory"
                        && child.Value == "PreserveNewest"
                    )
                    && element.Elements().Any(child =>
                        child.Name.LocalName == "CopyToPublishDirectory"
                        && child.Value == "PreserveNewest"
                    )
                    && element.Elements().Any(child =>
                        child.Name.LocalName == "TargetPath"
                        && child.Value == @"Prerequisites\VC143\%(Filename)%(Extension)"
                    )
                ),
            "the app-local Visual C++ runtime is copied to build and publish outputs"
        );
        Check(
            projectDocument.Descendants()
                .Any(element =>
                    element.Name.LocalName == "PackageReference"
                    && element.Attribute("Include")?.Value == "System.Formats.Nrbf"
                    && element.Attribute("Version")?.Value == "9.0.1"
                ),
            "the Windows XAML compiler can resolve the H.NotifyIcon drawing dependency graph"
        );
        Check(
            projectDocument.Descendants()
                .Any(element =>
                    element.Name.LocalName == "PackageDownload"
                    && element.Attribute("Include")?.Value == "System.Security.Permissions"
                    && element.Attribute("Version")?.Value == "[6.0.0]"
                ),
            "the managed XAML compiler compatibility dependency is restored"
        );
        Check(
            projectDocument.Descendants()
                .Any(element =>
                    element.Name.LocalName == "Target"
                    && element.Attribute("Name")?.Value == "CopyProjectPriToPublishDirectory"
                    && element.Attribute("AfterTargets")?.Value == "Publish"
                    && element.Descendants().Any(child =>
                        child.Name.LocalName == "Copy"
                        && child.Attribute("SourceFiles")?.Value == "$(ProjectPriFullPath)"
                        && child.Attribute("DestinationFolder")?.Value == "$(PublishDir)"
                    )
                ),
            "the unpackaged Windows publish copies the generated project resource index"
        );
        Check(
            projectDocument.Descendants()
                .Any(element =>
                    element.Name.LocalName == "ProjectPriIndexName"
                    && element.Value == "HerdMe.Windows"
                ),
            "the unpackaged Windows resource index has a stable application map name"
        );

        var windowsDirectory = Path.Combine(repositoryRoot, "Windows");
        Check(
            !File.Exists(Path.Combine(windowsDirectory, "Directory.Build.targets")),
            "the Windows build does not override the XAML compiler after SDK imports"
        );
        var buildScript = File.ReadAllText(Path.Combine(windowsDirectory, "build.ps1"));
        Check(
            buildScript.Contains("/p:UseXamlCompilerExecutable=false", StringComparison.Ordinal),
            "the native Windows build uses the managed XAML compiler through Visual Studio MSBuild"
        );
        Check(
            buildScript.Contains("-Filter \"*.xaml\" -File", StringComparison.Ordinal)
                && buildScript.Contains(
                    "$_ -in @(\"bin\", \"obj\")",
                    StringComparison.Ordinal
                ),
            "the repeated Windows build validates only source XAML files"
        );
        var buildTools = File.ReadAllText(
            Path.Combine(windowsDirectory, "windows-build-tools.ps1")
        );
        Check(
            buildTools.Contains("Find-HerdMeMSBuild", StringComparison.Ordinal)
                && buildTools.Contains("System.Security.Permissions.dll", StringComparison.Ordinal)
                && buildTools.Contains("Copy-HerdMeVCRuntime", StringComparison.Ordinal)
                && buildTools.Contains("Microsoft.VC143.CRT", StringComparison.Ordinal)
                && buildTools.Contains("Get-AuthenticodeSignature", StringComparison.Ordinal)
                && buildScript.Contains(
                    "Copy-HerdMeVCRuntime -DestinationDirectory",
                    StringComparison.Ordinal
                ),
            "the Windows build locates signed Visual Studio build and app-local runtime dependencies"
        );
        var portablePackaging = File.ReadAllText(
            Path.Combine(windowsDirectory, "package-portable.ps1")
        );
        Check(
            portablePackaging.Contains("Find-HerdMeMSBuild", StringComparison.Ordinal)
                && portablePackaging.Contains("/t:Publish", StringComparison.Ordinal)
                && portablePackaging.Contains(
                    "/p:UseXamlCompilerExecutable=false",
                    StringComparison.Ordinal
                )
                && portablePackaging.Contains(
                    @"Prerequisites\VC143\vcruntime140.dll",
                    StringComparison.Ordinal
                ),
            "the portable Windows publish uses validated MSBuild and includes the PHP VC runtime"
        );
        Check(
            File.ReadAllText(Path.Combine(windowsDirectory, "check-format.ps1"))
                .Contains(
                    "HerdMe.Windows.ContractTests/HerdMe.Windows.ContractTests.csproj",
                    StringComparison.Ordinal
                ),
            "C# formatting is checked through the cross-platform project without loading XAML"
        );

        var navigationContract = new[]
        {
            (Tag: "dashboard", NavigationId: "NavDashboard", Page: "DashboardPage.xaml", PageId: "DashboardPageRoot"),
            (Tag: "general", NavigationId: "NavGeneral", Page: "GeneralPage.xaml", PageId: "GeneralPageRoot"),
            (Tag: "sites", NavigationId: "NavSites", Page: "SitesPage.xaml", PageId: "SitesPageRoot"),
            (Tag: "php", NavigationId: "NavPhp", Page: "PhpPage.xaml", PageId: "PhpPageRoot"),
            (Tag: "node", NavigationId: "NavNode", Page: "NodePage.xaml", PageId: "NodePageRoot"),
            (Tag: "services", NavigationId: "NavServices", Page: "ServicesPage.xaml", PageId: "ServicesPageRoot"),
            (Tag: "mail", NavigationId: "NavMail", Page: "MailPage.xaml", PageId: "MailPageRoot"),
            (Tag: "dumps", NavigationId: "NavDumps", Page: "DumpsPage.xaml", PageId: "DumpsPageRoot"),
            (Tag: "debugger", NavigationId: "NavDebugger", Page: "DebuggerPage.xaml", PageId: "DebuggerPageRoot"),
            (Tag: "logs", NavigationId: "NavLogs", Page: "LogsPage.xaml", PageId: "LogsPageRoot"),
            (Tag: "about", NavigationId: "NavAbout", Page: "AboutPage.xaml", PageId: "AboutPageRoot")
        };
        var mainWindowDocument = XDocument.Load(Path.Combine(projectRoot, "MainWindow.xaml"));
        var navigationItems = mainWindowDocument.Descendants()
            .Where(element => element.Name.LocalName == "NavigationViewItem")
            .ToDictionary(
                element => element.Attribute("Tag")?.Value ?? string.Empty,
                element => element.Attribute("AutomationProperties.AutomationId")?.Value
                    ?? string.Empty,
                StringComparer.Ordinal
            );
        var acceptanceSource = File.ReadAllText(
            Path.Combine(repositoryRoot, "Windows", "acceptance.ps1")
        );
        foreach (var entry in navigationContract)
        {
            Check(
                navigationItems.GetValueOrDefault(entry.Tag) == entry.NavigationId,
                $"Windows navigation {entry.Tag} exposes stable automation id {entry.NavigationId}"
            );
            var pageRoot = XDocument.Load(
                Path.Combine(projectRoot, "Pages", entry.Page)
            ).Root;
            Check(
                pageRoot?.Attribute("AutomationProperties.AutomationId")?.Value == entry.PageId,
                $"Windows page {entry.Page} exposes stable automation id {entry.PageId}"
            );
            Check(
                acceptanceSource.Contains($"Navigation = \"{entry.NavigationId}\"", StringComparison.Ordinal)
                    && acceptanceSource.Contains($"Page = \"{entry.PageId}\"", StringComparison.Ordinal),
                $"native Windows acceptance opens {entry.Page} through UI Automation"
            );
        }
        Check(
            acceptanceSource.Contains("Assert-WinUiNavigation $primary", StringComparison.Ordinal),
            "native Windows acceptance executes the WinUI navigation smoke test"
        );
        Check(
            acceptanceSource.Contains("Assert-FixedMainWindow $primary", StringComparison.Ordinal)
                && acceptanceSource.Contains("CanMaximize", StringComparison.Ordinal)
                && acceptanceSource.Contains("CanMinimize", StringComparison.Ordinal)
                && acceptanceSource.Contains("CanResize", StringComparison.Ordinal),
            "native Windows acceptance rejects resizable main windows"
        );
        Check(
            acceptanceSource.Contains("Assert-OnboardingLayout $onboarding", StringComparison.Ordinal)
                && acceptanceSource.Contains("OnboardingStartButton", StringComparison.Ordinal)
                && acceptanceSource.Contains("IsOffscreen", StringComparison.Ordinal),
            "native Windows acceptance rejects clipped first-run onboarding"
        );
        var sitesPageCodeBehind = File.ReadAllText(
            Path.Combine(projectRoot, "Pages", "SitesPage.xaml.cs")
        );
        Check(
            sitesPageCodeBehind.Contains(
                "ScrollViewer.SetHorizontalScrollBarVisibility(editor",
                StringComparison.Ordinal
            )
                && sitesPageCodeBehind.Contains(
                    "ScrollViewer.SetVerticalScrollBarVisibility(editor",
                    StringComparison.Ordinal
            ),
            "the environment editor configures TextBox scrollbars with WinUI attached properties"
        );

        foreach (var modelType in new[]
        {
            typeof(SiteRecord),
            typeof(CapturedDump),
            typeof(RuntimeCheck),
            typeof(LogSourceRecord),
            typeof(LogFileRecord),
            typeof(CapturedMail),
            typeof(NodeRuntimeRow),
            typeof(ManagedServiceRow)
        })
        {
            Check(
                modelType.GetConstructor(Type.EmptyTypes) is not null,
                $"Windows XAML model {modelType.Name} has a public parameterless constructor"
            );
            var writableProperties = modelType.GetProperties()
                .Where(property => property.SetMethod is not null)
                .ToArray();
            Check(
                writableProperties.All(property =>
                    property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
                        .All(modifier =>
                            modifier.FullName != "System.Runtime.CompilerServices.IsExternalInit"
                        )
                        && property.CustomAttributes.All(attribute =>
                            attribute.AttributeType.FullName
                                != "System.Runtime.CompilerServices.RequiredMemberAttribute"
                        )
                ),
                $"Windows XAML model {modelType.Name} exposes mutable, non-required properties to generated bindings"
            );
        }
    }

    internal static void VerifyLocalizationContracts(string repositoryRoot)
    {
        var projectRoot = Path.Combine(repositoryRoot, "Windows", "HerdMe.Windows");
        var englishPath = Path.Combine(projectRoot, "Strings", "en-US", "Resources.resw");
        var arabicPath = Path.Combine(projectRoot, "Strings", "ar", "Resources.resw");
        var english = ReadResw(englishPath);
        var arabic = ReadResw(arabicPath);

        Check(
            english.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(arabic.Keys),
            "Windows English and Arabic resources expose the same keys"
        );
        Check(
            english.Values.All(value => !string.IsNullOrWhiteSpace(value))
                && arabic.Values.All(value => !string.IsNullOrWhiteSpace(value)),
            "Windows localization resources contain no empty values"
        );
        var invariantTranslationKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "NavPhp.Content",
            "NavNode.Content",
            "OnboardingSetupSummaryText",
            "PhpPageTitle.Text",
            "NodePageTitle.Text",
            "PhpComposerVersion",
            "PhpLaravelInstallerVersion",
            "ServicesTablePlusButton.Text",
            "SitesProgressRow",
            "SitesArtisanDialogTitle",
            "DebuggerXdebugTitle.Text"
        };
        var untranslatedKeys = english.Keys
            .Where(key => english[key] == arabic[key] && !invariantTranslationKeys.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Check(
            untranslatedKeys.Length == 0,
            $"Windows Arabic resources translate every user-facing value: {string.Join(", ", untranslatedKeys)}"
        );
        var nonArabicTranslationKeys = english.Keys
            .Where(key => !invariantTranslationKeys.Contains(key))
            .Where(key => !Regex.IsMatch(arabic[key], @"[\u0600-\u06FF]"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Check(
            nonArabicTranslationKeys.Length == 0,
            $"Windows Arabic resources contain Arabic text: {string.Join(", ", nonArabicTranslationKeys)}"
        );
        var onboardingSource = File.ReadAllText(
            Path.Combine(projectRoot, "Views", "OnboardingView.xaml.cs")
        );
        var onboardingXaml = File.ReadAllText(
            Path.Combine(projectRoot, "Views", "OnboardingView.xaml")
        );
        Check(
            onboardingSource.Contains(
                "\"OnboardingSetupSummaryText\"",
                StringComparison.Ordinal
            )
                && !onboardingSource.Contains(
                    "\"OnboardingSetupSummary.Text\"",
                    StringComparison.Ordinal
            ),
            "the onboarding summary uses a direct ResourceLoader key at runtime"
        );
        Check(
            onboardingXaml.Contains("VerticalScrollBarVisibility=\"Auto\"", StringComparison.Ordinal)
                && onboardingXaml.Contains("TextWrapping=\"Wrap\"", StringComparison.Ordinal)
                && onboardingSource.Contains("OnboardingFailureMessage", StringComparison.Ordinal)
                && !onboardingSource.Contains("FailureText.Text = error.Message", StringComparison.Ordinal),
            "onboarding stays scrollable and keeps raw failures in technical details"
        );
        var phpInstallerSource = File.ReadAllText(Path.Combine(
            projectRoot,
            "Services",
            "PhpRuntimeInstaller.cs"
        ));
        Check(
            phpInstallerSource.Contains("extension = zip", StringComparison.Ordinal)
                && phpInstallerSource.Contains("extension = exif", StringComparison.Ordinal)
                && phpInstallerSource.Contains("extension = intl", StringComparison.Ordinal)
                && phpInstallerSource.Contains("php_{extension}.dll", StringComparison.Ordinal)
                && phpInstallerSource.Contains("HasRequiredConfiguration", StringComparison.Ordinal),
            "managed PHP enables and validates ZIP for Composer on clean Windows hosts"
        );
        var composerToolsSource = File.ReadAllText(Path.Combine(
            projectRoot,
            "Services",
            "ComposerToolManager.cs"
        ));
        Check(
            composerToolsSource.Contains("ComposerCommandPath", StringComparison.Ordinal)
                && composerToolsSource.Contains("EnsureComposerCommand", StringComparison.Ordinal)
                && composerToolsSource.Contains(
                    @"%~dp0..\\Runtimes\\php\\",
                    StringComparison.Ordinal
                ),
            "Laravel project creation exposes managed Composer to child commands on clean hosts"
        );
        var gitInstallerSource = File.ReadAllText(Path.Combine(
            projectRoot,
            "Services",
            "GitRuntimeInstaller.cs"
        ));
        var userPathSource = File.ReadAllText(Path.Combine(
            projectRoot,
            "Services",
            "WindowsUserPathManager.cs"
        ));
        var initialSetupSource = File.ReadAllText(Path.Combine(
            projectRoot,
            "Services",
            "InitialSetupManager.cs"
        ));
        var projectCreatorSource = File.ReadAllText(Path.Combine(
            projectRoot,
            "Services",
            "LaravelProjectCreator.cs"
        ));
        Check(
            gitInstallerSource.Contains("git-for-windows/git/releases/latest", StringComparison.Ordinal)
                && gitInstallerSource.Contains("MinGitArchivePattern", StringComparison.Ordinal)
                && gitInstallerSource.Contains("sha256:", StringComparison.Ordinal)
                && gitInstallerSource.Contains("release.Size", StringComparison.Ordinal),
            "managed Git uses the official MinGit x64 asset with size and SHA-256 verification"
        );
        Check(
            initialSetupSource.Contains("EnsureCommandLineToolsAsync", StringComparison.Ordinal)
                && initialSetupSource.Contains("InitialSetupStage.Git", StringComparison.Ordinal)
                && initialSetupSource.Contains("gitInstaller.EnsureInstalledAsync", StringComparison.Ordinal)
                && initialSetupSource.Contains(
                    "phpInstaller.EnsureInstalledConfigurationsAsync(cancellationToken)",
                    StringComparison.Ordinal
                )
                && initialSetupSource.Contains("userPathManager.Synchronize", StringComparison.Ordinal),
            "first-run and upgrade repair install Git with the complete command-line toolchain"
        );
        Check(
            projectCreatorSource.Contains("gitInstaller.EnsureInstalledAsync", StringComparison.Ordinal)
                && !projectCreatorSource.Contains("\"git.exe\"", StringComparison.Ordinal),
            "Laravel project creation uses managed Git instead of requiring system Git"
        );
        var phpRepairIndex = projectCreatorSource.IndexOf(
            "await phpInstaller.EnsureManagedConfigurationAsync(settings.PhpCycle, cancellationToken)",
            StringComparison.Ordinal
        );
        var laravelInstallerIndex = projectCreatorSource.IndexOf(
            "tools.EnsureLaravelInstallerAsync(settings.PhpCycle",
            StringComparison.Ordinal
        );
        Check(
            phpRepairIndex >= 0 && phpRepairIndex < laravelInstallerIndex,
            "Laravel project creation repairs managed PHP before Composer starts"
        );
        Check(
            userPathSource.Contains("EnvironmentVariableTarget.User", StringComparison.Ordinal)
                && userPathSource.Contains("SendMessageTimeout", StringComparison.Ordinal)
                && userPathSource.Contains("WmSettingChange", StringComparison.Ordinal),
            "the managed PHP, Composer, Laravel, Node, npm, and Git paths persist for new CMD sessions"
        );
        foreach (var key in english.Keys)
        {
            var englishPlaceholders = Regex.Matches(english[key], @"\{(?<index>\d+)(?:[^{}]*)\}")
                .Select(match => match.Groups["index"].Value)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var arabicPlaceholders = Regex.Matches(arabic[key], @"\{(?<index>\d+)(?:[^{}]*)\}")
                .Select(match => match.Groups["index"].Value)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Check(
                englishPlaceholders.SequenceEqual(arabicPlaceholders),
                $"Windows localization resource {key} preserves format placeholders"
            );
        }

        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        var localizedXamlPaths = Directory.EnumerateFiles(
            projectRoot,
            "*.xaml",
            SearchOption.AllDirectories
        ).Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Check(localizedXamlPaths.Length > 0, "Windows localization discovers XAML surfaces");

        var localizedElements = localizedXamlPaths.SelectMany(path =>
        {
            var root = XDocument.Load(path).Root
                ?? throw new InvalidDataException($"The XAML document {path} has no root element.");
            return root.DescendantsAndSelf()
                .Select(element => new
                {
                    Path = path,
                    Element = element,
                    Uid = element.Attribute(xamlNamespace + "Uid")?.Value
                });
        }).ToArray();
        var localizedUids = localizedElements
            .Select(item => item.Uid)
            .Where(uid => !string.IsNullOrWhiteSpace(uid))
            .Select(uid => uid!)
            .ToArray();
        Check(localizedUids.Length > 0, "Windows XAML surfaces declare localization identifiers");
        Check(
            localizedUids.Distinct(StringComparer.Ordinal).Count() == localizedUids.Length,
            "Windows localization identifiers are unique across localized surfaces"
        );
        foreach (var uid in localizedUids)
        {
            Check(
                english.Keys.Any(key => key.StartsWith(uid + ".", StringComparison.Ordinal)),
                $"Windows localization identifier {uid} has an English resource"
            );
        }

        var localizedUidSet = localizedUids.ToHashSet(StringComparer.Ordinal);
        var orphanedXamlResources = english.Keys
            .Where(key => key.Contains('.', StringComparison.Ordinal))
            .Where(key => !localizedUidSet.Contains(key[..key.IndexOf('.', StringComparison.Ordinal)]))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Check(
            orphanedXamlResources.Length == 0,
            $"Windows XAML resources belong to current surfaces: {string.Join(", ", orphanedXamlResources)}"
        );

        var localizableAttributeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Text",
            "Content",
            "Header",
            "PlaceholderText",
            "Label",
            "ToolTipText",
            "Title",
            "CloseButtonText",
            "PrimaryButtonText",
            "SecondaryButtonText",
            "AutomationProperties.Name",
            "AutomationProperties.HelpText",
            "ToolTipService.ToolTip"
        };
        var invariantXamlValues = new HashSet<string>(StringComparer.Ordinal)
        {
            ".env",
            "H",
            "HerdMe",
            "test"
        };
        static string XamlResourcePropertyName(string attributeName)
        {
            if (attributeName.StartsWith("AutomationProperties.", StringComparison.Ordinal))
            {
                return $"[using:Microsoft.UI.Xaml.Automation]{attributeName}";
            }
            if (attributeName.StartsWith("ToolTipService.", StringComparison.Ordinal))
            {
                return $"[using:Microsoft.UI.Xaml.Controls]{attributeName}";
            }
            return attributeName;
        }
        var unlocalizedXamlValues = localizedElements.SelectMany(item =>
        {
            var uid = item.Uid;
            return item.Element.Attributes()
                .Where(attribute => attribute.Name.NamespaceName.Length == 0)
                .Where(attribute => localizableAttributeNames.Contains(attribute.Name.LocalName))
                .Where(attribute =>
                {
                    var value = attribute.Value.Trim();
                    return value.Length > 0
                        && !value.StartsWith('{')
                        && !invariantXamlValues.Contains(value)
                        && !Regex.IsMatch(value, @"^[\d\s.,:+\-/%]+$");
                })
                .Where(attribute => string.IsNullOrWhiteSpace(uid)
                    || !english.ContainsKey(
                        $"{uid}.{XamlResourcePropertyName(attribute.Name.LocalName)}"
                    ))
                .Select(attribute =>
                    $"{Path.GetRelativePath(repositoryRoot, item.Path)}: {item.Element.Name.LocalName}.{attribute.Name.LocalName}");
        }).Order(StringComparer.Ordinal).ToArray();
        Check(
            unlocalizedXamlValues.Length == 0,
            $"Windows XAML literals use localized resources: {string.Join(", ", unlocalizedXamlValues)}"
        );

        var directLocalizationPattern = new Regex(
            @"AppLocalization\.(?:Get|Format)\(\s*""(?<key>[^""\r\n]+)""",
            RegexOptions.CultureInvariant
        );
        var missingDirectLocalizationKeys = Directory.EnumerateFiles(
            projectRoot,
            "*.cs",
            SearchOption.AllDirectories
        ).Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj"))
            .SelectMany(path => directLocalizationPattern.Matches(File.ReadAllText(path))
                .Select(match => match.Groups["key"].Value))
            .Distinct(StringComparer.Ordinal)
            .Where(key => !english.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Check(
            missingDirectLocalizationKeys.Length == 0,
            $"Windows dynamic localization keys exist: {string.Join(", ", missingDirectLocalizationKeys)}"
        );

        var servicesPageSource = File.ReadAllText(
            Path.Combine(projectRoot, "Pages", "ServicesPage.xaml.cs")
        );
        var servicesPageXaml = File.ReadAllText(
            Path.Combine(projectRoot, "Pages", "ServicesPage.xaml")
        );
        Check(
            servicesPageSource.Contains("StartAutomatically = true", StringComparison.Ordinal)
                && servicesPageSource.Contains("await manager.StartAsync(instance.Id)", StringComparison.Ordinal)
                && servicesPageSource.Contains("manager.Changed += Manager_Changed", StringComparison.Ordinal)
                && servicesPageSource.Contains("previous?.Cancel()", StringComparison.Ordinal)
                && servicesPageXaml.Contains("Unloaded=\"Page_Unloaded\"", StringComparison.Ordinal)
                && servicesPageXaml.Contains("Click=\"Toggle_Click\"", StringComparison.Ordinal)
                && servicesPageXaml.Contains("ServicesMoreTooltip", StringComparison.Ordinal)
                && servicesPageXaml.Contains("Mode=OneTime", StringComparison.Ordinal),
            "Windows Services keeps installation state across navigation and exposes unclipped controls"
        );
        Check(
            servicesPageSource.IndexOf("RenderRows(", StringComparison.Ordinal)
                < servicesPageSource.IndexOf("await Task.WhenAll", StringComparison.Ordinal),
            "Windows Services renders persisted rows before remote update checks complete"
        );
        var servicesResourceKeys = Regex.Matches(
            servicesPageSource,
            @"""(?<key>Services[A-Za-z0-9]+)""",
            RegexOptions.CultureInvariant
        ).Select(match => match.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Check(
            servicesResourceKeys.Length > 0
                && servicesResourceKeys.All(english.ContainsKey),
            "Windows Services dynamic display strings use localized resources"
        );

        var mailPageSource = File.ReadAllText(
            Path.Combine(projectRoot, "Pages", "MailPage.xaml.cs")
        );
        var mailPageXaml = File.ReadAllText(
            Path.Combine(projectRoot, "Pages", "MailPage.xaml")
        );
        Check(
            mailPageSource.Contains("ServiceEnvironmentFile.Update(", StringComparison.Ordinal)
                && mailPageSource.Contains("mail.Port ?? MailCaptureService.DefaultPort", StringComparison.Ordinal)
                && !mailPageSource.Contains("Clipboard.SetContent", StringComparison.Ordinal)
                && mailPageXaml.Contains("Click=\"AddToEnvironment_Click\"", StringComparison.Ordinal)
                && mailPageXaml.Contains("x:Uid=\"MailEnvironmentButton\"", StringComparison.Ordinal),
            "Windows Mail selects a site and writes its active SMTP settings directly to .env"
        );
        Check(
            mailPageSource.Contains("if (message is null)", StringComparison.Ordinal)
                && mailPageSource.Contains(
                    "if (!loaded || !IsCurrentPreviewSelection()) return;",
                    StringComparison.Ordinal
                )
                && mailPageSource.Contains(
                    "CoreWebView2WebErrorStatus.OperationCanceled",
                    StringComparison.Ordinal
                )
                && mailPageSource.Contains(
                    "CoreWebView2WebErrorStatus.ConnectionAborted",
                    StringComparison.Ordinal
                )
                && mailPageXaml.Contains(
                    "x:Name=\"HtmlPreview\"",
                    StringComparison.Ordinal
                )
                && mailPageXaml.Contains(
                    "Visibility=\"Collapsed\"",
                    StringComparison.Ordinal
                ),
            "Windows Mail keeps an empty inbox neutral and ignores replaced WebView navigation"
        );

        var sitesPageSource = File.ReadAllText(
            Path.Combine(projectRoot, "Pages", "SitesPage.xaml.cs")
        );
        var sitesResourceKeys = Regex.Matches(
            sitesPageSource,
            @"""(?<key>Sites[A-Za-z0-9]+)""",
            RegexOptions.CultureInvariant
        ).Select(match => match.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Check(
            sitesResourceKeys.Length > 0
                && sitesResourceKeys.All(english.ContainsKey),
            "Windows Sites dynamic display strings use localized resources"
        );
        var siteScanStart = sitesPageSource.IndexOf(
            "private async Task ScanAsync",
            StringComparison.Ordinal
        );
        var siteScanEnd = siteScanStart < 0
            ? -1
            : sitesPageSource.IndexOf(
                "private void StartGitInspection",
                siteScanStart,
                StringComparison.Ordinal
            );
        Check(
            siteScanStart >= 0
                && siteScanEnd > siteScanStart
                && !sitesPageSource[siteScanStart..siteScanEnd].Contains(
                    "SaveRoots(",
                    StringComparison.Ordinal
                ),
            "automatic site scans never overwrite the persisted park roots"
        );
        Check(
            sitesPageSource.Contains(
                "private bool suppressPreviewToggle = true;",
                StringComparison.Ordinal
            )
                && sitesPageSource.Contains(
                    "settingsStore.UpdateShowPreviews(PreviewToggle.IsOn);",
                    StringComparison.Ordinal
                ),
            "Windows Sites initialization and preview changes never overwrite park roots"
        );
        Check(
            sitesPageSource.Contains(
                "if (!loaded || IsExpectedNavigationCancellation(args.WebErrorStatus)) return;",
                StringComparison.Ordinal
            )
                && sitesPageSource.Contains(
                    "CoreWebView2WebErrorStatus.OperationCanceled",
                    StringComparison.Ordinal
                )
                && sitesPageSource.Contains(
                    "CoreWebView2WebErrorStatus.ConnectionAborted",
                    StringComparison.Ordinal
                ),
            "Windows Sites ignores WebView cancellations caused by navigation replacement or unload"
        );
        Check(
            sitesPageSource.Contains("DisplayOption", StringComparison.Ordinal)
                && sitesPageSource.Contains("selectedPreset.Value", StringComparison.Ordinal)
                && sitesPageSource.Contains("siteRuntimeStore.SetPhp", StringComparison.Ordinal),
            "Windows Sites keeps localized labels separate from runtime and command values"
        );
        var sitesPageXaml = File.ReadAllText(
            Path.Combine(projectRoot, "Pages", "SitesPage.xaml")
        );
        var generalPageXaml = File.ReadAllText(
            Path.Combine(projectRoot, "Pages", "GeneralPage.xaml")
        );
        var generalPageSource = File.ReadAllText(
            Path.Combine(projectRoot, "Pages", "GeneralPage.xaml.cs")
        );
        Check(
            !sitesPageXaml.Contains("EnvironmentButton", StringComparison.Ordinal)
                && !sitesPageXaml.Contains("TldTextBox", StringComparison.Ordinal)
                && generalPageXaml.Contains("x:Name=\"TldTextBox\"", StringComparison.Ordinal)
                && sitesPageSource.Contains("SynchronizeSitesAsync(scanned)", StringComparison.Ordinal),
            "Windows Sites starts automatically and keeps the domain suffix in General"
        );
        Check(
            generalPageSource.Contains("ManagedRuntimeChecks()", StringComparison.Ordinal)
                && !generalPageSource.Contains("report.Runtimes", StringComparison.Ordinal)
                && new[] { "composer", "laravel", "node", "npm", "git" }.All(tool =>
                    generalPageSource.Contains($"\"{tool}\"", StringComparison.Ordinal)),
            "Windows General reports HerdMe-managed command-line tools instead of the launch PATH"
        );
        Check(
            new[]
            {
                "EnvironmentFileText", "LogsText", "RoutesText", "GitText",
                "AssociatedServicesText"
            }.All(name => sitesPageXaml.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal))
                && sitesPageSource.Contains("InspectSiteDetails", StringComparison.Ordinal)
                && sitesPageSource.Contains("ParseEnvironment", StringComparison.Ordinal)
                && sitesPageSource.Contains("InspectGit", StringComparison.Ordinal)
                && sitesPageSource.Contains("MatchesService", StringComparison.Ordinal),
            "Windows Sites exposes complete environment, log, route, Git, and service details"
        );

        foreach (var page in new[]
        {
            "Mail",
            "Dumps",
            "Debugger",
            "Logs",
            "About"
        })
        {
            var pageSource = File.ReadAllText(
                Path.Combine(projectRoot, "Pages", page + "Page.xaml.cs")
            );
            var resourceKeys = Regex.Matches(
                pageSource,
                $@"""(?<key>{page}[A-Za-z0-9]+)""",
                RegexOptions.CultureInvariant
            ).Select(match => match.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
            Check(
                resourceKeys.Length > 0
                    && resourceKeys.All(english.ContainsKey),
                $"Windows {page} dynamic display strings use localized resources"
            );
        }

        foreach (var stage in Enum.GetNames<LaravelProjectCreationStage>())
        {
            Check(
                english.ContainsKey($"SitesProjectStage{stage}Title")
                    && english.ContainsKey($"SitesProjectStage{stage}Detail"),
                $"Windows Laravel project stage {stage} has localized title and detail text"
            );
        }

        foreach (var stage in Enum.GetNames<InitialSetupStage>())
        {
            Check(
                english.ContainsKey($"OnboardingStage{stage}Title")
                    && english.ContainsKey($"OnboardingStage{stage}Detail"),
                $"Windows onboarding stage {stage} has localized title and detail text"
            );
        }
        Check(
            UiLanguagePolicy.IsRightToLeft("ar-BH")
                && !UiLanguagePolicy.IsRightToLeft("en-US"),
            "Windows layout direction follows Arabic and English UI cultures"
        );

        var localizationSource = File.ReadAllText(Path.Combine(
            projectRoot,
            "Services",
            "AppLocalization.cs"
        ));
        var windowSource = File.ReadAllText(Path.Combine(projectRoot, "MainWindow.xaml.cs"));
        Check(
            localizationSource.Contains("Microsoft.Windows.ApplicationModel.Resources", StringComparison.Ordinal)
                && localizationSource.Contains("new ResourceLoader(", StringComparison.Ordinal)
                && localizationSource.Contains("HerdMe.Windows.pri", StringComparison.Ordinal)
                && localizationSource.Contains("\"Resources\"", StringComparison.Ordinal)
                && localizationSource.Contains("Loader.Value.GetString(key)", StringComparison.Ordinal)
                && localizationSource.Contains("COMException or FileNotFoundException", StringComparison.Ordinal)
                && !localizationSource.Contains("new ResourceLoader()", StringComparison.Ordinal)
                && !localizationSource.Contains("MainResourceMap.GetValue", StringComparison.Ordinal)
                && localizationSource.Contains("ApplicationLanguages.Languages", StringComparison.Ordinal),
            "Windows localization explicitly loads strings from the unpackaged MRT Core resource index"
        );
        Check(
            windowSource.Contains("RootLayout.Language = AppLocalization.LanguageTag", StringComparison.Ordinal)
                && windowSource.Contains("RootLayout.FlowDirection = AppLocalization.LayoutDirection", StringComparison.Ordinal),
            "the Windows root visual applies localized language and layout direction"
        );
    }

    internal static Dictionary<string, string> ReadResw(string path)
    {
        Check(File.Exists(path), $"{path} exists");
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        var document = XDocument.Load(reader);
        return document.Root?.Elements("data").ToDictionary(
            element => element.Attribute("name")?.Value
                ?? throw new InvalidDataException($"A resource in {path} has no name."),
            element => element.Element("value")?.Value
                ?? throw new InvalidDataException($"A resource in {path} has no value."),
            StringComparer.Ordinal
        ) ?? throw new InvalidDataException($"The resource file {path} has no root element.");
    }

    internal static void VerifyArtisanCommandContracts(string repositoryRoot)
    {
        Check(
            ArtisanCommandCatalog.Suggestions.Contains("route:list", StringComparer.Ordinal)
                && ArtisanCommandCatalog.Suggestions.Contains("optimize:clear", StringComparer.Ordinal)
                && ArtisanCommandCatalog.Suggestions.Count >= 20,
            "Artisan custom commands expose useful autocomplete suggestions"
        );
        var discoveredSuggestions = ArtisanCommandCatalog.ParseCommandListJson(
            """
            {
              "commands": [
                { "name": "route:list" },
                { "name": "vendor:package-command" },
                { "name": "vendor:package-command" }
              ]
            }
            """
        );
        Check(
            discoveredSuggestions.SequenceEqual(["route:list", "vendor:package-command"])
                && ArtisanCommandCatalog.MergeSuggestions(discoveredSuggestions)
                    .Contains("vendor:package-command", StringComparer.Ordinal),
            "Artisan autocomplete discovers every command registered by the project"
        );
        Check(
            ArtisanCommandCatalog.ParseCommandListJson(
                """{ "commands": { "about": {}, "package:sync": {} } }"""
            ).SequenceEqual(["about", "package:sync"]),
            "Artisan autocomplete accepts Symfony object-shaped command lists"
        );
        Check(
            ArtisanCommandCatalog.Parse(
                "artisan route:list --path='api v1' --name=\"users.show\""
            ).SequenceEqual(["route:list", "--path=api v1", "--name=users.show"]),
            "Artisan commands preserve quoted arguments without a shell"
        );
        var queue = ArtisanCommandCatalog.Resolve("queue-work", string.Empty);
        Check(
            queue.Arguments.SequenceEqual(["queue:work", "--no-interaction"])
                && queue.Timeout == TimeSpan.FromHours(24),
            "Artisan queue workers remain cancellable long-running commands"
        );
        Throws<ArgumentException>(
            () => ArtisanCommandCatalog.Parse("php artisan route:list"),
            "Artisan rejects executable and shell-style command prefixes"
        );
        Throws<ArgumentException>(
            () => ArtisanCommandCatalog.Parse("route:list\nconfig:clear"),
            "Artisan rejects control characters"
        );
        Throws<ArgumentException>(
            () => ArtisanCommandCatalog.Parse(
                string.Join(' ', new[] { "route:list" }.Concat(Enumerable.Repeat("--flag", 32)))
            ),
            "Artisan bounds argument counts"
        );

        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Windows",
            "HerdMe.Windows",
            "Services",
            "ArtisanCommandRunner.cs"
        ));
        Check(
            source.Contains("CreateNoWindow = true", StringComparison.Ordinal)
                && source.Contains("UseShellExecute = false", StringComparison.Ordinal)
                && !source.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase)
                && !source.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase),
            "Artisan runs without a visible console or command shell"
        );
        var sitesSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Windows",
            "HerdMe.Windows",
            "Pages",
            "SitesPage.xaml.cs"
        ));
        Check(
            sitesSource.Contains("new AutoSuggestBox", StringComparison.Ordinal)
                && sitesSource.Contains("ArtisanCommandCatalog.Suggestions", StringComparison.Ordinal)
                && sitesSource.Contains("DiscoverCommandsAsync", StringComparison.Ordinal)
                && sitesSource.Contains("AccentButtonStyle", StringComparison.Ordinal)
                && sitesSource.Contains("content.Children.Add(buttons);", StringComparison.Ordinal)
                && Regex.Matches(
                    sitesSource,
                    @"new StackPanel \{ Spacing = 12, Width = 500 \}"
                ).Count == 2
                && sitesSource.Contains("MinWidth = 440", StringComparison.Ordinal)
                && !sitesSource.Contains("MinWidth = 620", StringComparison.Ordinal),
            "Artisan and npm dialogs expose visible primary actions and autocomplete"
        );
    }

    internal static async Task VerifyNpmScriptContractsAsync(string repositoryRoot)
    {
        var root = Path.Combine(Path.GetTempPath(), "herdme-npm-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var packagePath = Path.Combine(root, "package.json");
            File.WriteAllText(
                packagePath,
                JsonSerializer.Serialize(new
                {
                    name = "npm-contract",
                    scripts = new Dictionary<string, object>
                    {
                        ["zeta"] = "echo zeta",
                        ["test"] = "echo test",
                        ["build"] = "echo build",
                        ["dev"] = "echo dev",
                        ["alpha"] = "echo alpha",
                        ["-unsafe"] = "echo unsafe",
                        ["bad\nname"] = "echo unsafe",
                        ["non-string"] = 42
                    }
                })
            );
            Check(
                NpmScriptCatalog.Discover(root).Select(script => script.Name).SequenceEqual(
                    ["dev", "build", "test", "alpha", "zeta"]
                ),
                "npm scripts are parsed as structured JSON and ordered predictably"
            );
            Check(
                NpmScriptCatalog.TimeoutFor("dev") == TimeSpan.FromHours(24)
                    && NpmScriptCatalog.TimeoutFor("build") == TimeSpan.FromMinutes(30),
                "npm long-running scripts and bounded scripts use appropriate timeouts"
            );
            Throws<NpmScriptException>(
                () => NpmScriptCatalog.ValidateName("-unsafe"),
                "npm rejects option-like script names"
            );

            File.WriteAllText(packagePath, "{");
            Throws<NpmScriptException>(
                () => NpmScriptCatalog.Discover(root),
                "npm rejects malformed package.json files"
            );
            File.WriteAllBytes(packagePath, Enumerable.Repeat((byte)' ', 1 * 1_024 * 1_024 + 1).ToArray());
            Throws<NpmScriptException>(
                () => NpmScriptCatalog.Discover(root),
                "npm bounds package.json reads"
            );

            File.WriteAllText(
                packagePath,
                JsonSerializer.Serialize(new { scripts = new Dictionary<string, string> { ["build production"] = "echo build" } })
            );
            var npmCli = Path.Combine(root, "npm-cli.js");
            File.WriteAllText(npmCli, "fixture");
            var executable = ContractExecutablePath();
            var argumentsInvocation = new NpmScriptInvocation(
                executable,
                npmCli,
                root,
                "build production",
                NpmFixtureEnvironment("arguments"),
                TimeSpan.FromSeconds(5)
            );
            var result = await NpmScriptRunner.RunAsync(argumentsInvocation);
            var receivedArguments = JsonSerializer.Deserialize<string[]>(result.Output) ?? [];
            Check(
                result.ExitCode == 0
                    && receivedArguments.SequenceEqual(
                        [npmCli, "--no-update-notifier", "run", "build production"]
                    ),
                "npm invokes managed Node directly and preserves script names as one argument"
            );

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var delayInvocation = argumentsInvocation with
            {
                ScriptName = "dev",
                Environment = NpmFixtureEnvironment("delay"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            await ThrowsAsync<OperationCanceledException>(
                () => NpmScriptRunner.RunAsync(delayInvocation, cancellationToken: cancellation.Token),
                "npm cancellation terminates the managed process tree"
            );

            var supportRoot = Path.Combine(root, "support");
            var installer = new NodeRuntimeInstaller(supportRoot);
            var runtime = Path.Combine(installer.RuntimeRoot, "22.10.0");
            Directory.CreateDirectory(Path.Combine(runtime, "node_modules", "npm", "bin"));
            File.WriteAllText(Path.Combine(runtime, "node.exe"), "fixture");
            File.WriteAllText(Path.Combine(runtime, "node_modules", "npm", "bin", "npm-cli.js"), "fixture");
            var resolved = NpmScriptRunner.CreateInvocation(
                installer,
                root,
                "22",
                "build production"
            );
            Check(
                resolved.NodeExecutable == Path.Combine(runtime, "node.exe")
                    && resolved.NpmCli == Path.Combine(runtime, "node_modules", "npm", "bin", "npm-cli.js")
                    && resolved.Environment["PATH"].StartsWith(runtime, StringComparison.Ordinal),
                "npm resolves the requested HerdMe-managed Node major and npm CLI"
            );
            Throws<ArgumentException>(
                () => NpmScriptRunner.CreateToolInvocation(
                    installer,
                    root,
                    "22",
                    ["run", "arbitrary-command"],
                    TimeSpan.FromMinutes(5)
                ),
                "npm workflow tools reject commands outside the install, update, and audit whitelist"
            );

            var source = File.ReadAllText(Path.Combine(
                repositoryRoot,
                "Windows",
                "HerdMe.Windows",
                "Services",
                "NpmScriptRunner.cs"
            ));
            Check(
                source.Contains("CreateNoWindow = true", StringComparison.Ordinal)
                    && source.Contains("UseShellExecute = false", StringComparison.Ordinal)
                    && source.Contains("ArgumentList.Add(invocation.ScriptName)", StringComparison.Ordinal)
                    && !source.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase)
                    && !source.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase),
                "npm runs without a visible console or command shell"
            );
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    internal static async Task VerifySiteWorkflowArchiveContractsAsync()
    {
        Check(
            !SiteWorkflowArchive.ShouldInclude("storage/framework/CACHE/data", directory: true)
                && !SiteWorkflowArchive.ShouldInclude("Storage/Framework/Views/page.php", directory: false)
                && !SiteWorkflowArchive.ShouldInclude("vendor/package/file.php", directory: false)
                && SiteWorkflowArchive.ShouldInclude("app/Models/User.php", directory: false),
            "portable project exports apply case-insensitive cache and dependency exclusions"
        );

        var root = Path.Combine(
            Path.GetTempPath(),
            "herdme-export-contract-" + Guid.NewGuid().ToString("N")
        );
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "app"));
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        Directory.CreateDirectory(Path.Combine(project, "vendor", "package"));
        Directory.CreateDirectory(Path.Combine(project, "node_modules", "package"));
        Directory.CreateDirectory(Path.Combine(project, "storage", "logs"));
        Directory.CreateDirectory(Path.Combine(project, "storage", "framework", "CACHE"));
        File.WriteAllText(Path.Combine(project, "app", "index.php"), "<?php echo 'ok';");
        File.WriteAllText(Path.Combine(project, ".env"), "APP_ENV=local");
        File.WriteAllText(Path.Combine(project, ".git", "config"), "excluded");
        File.WriteAllText(Path.Combine(project, "vendor", "package", "file.php"), "excluded");
        File.WriteAllText(Path.Combine(project, "node_modules", "package", "index.js"), "excluded");
        File.WriteAllText(Path.Combine(project, "storage", "logs", "laravel.log"), "excluded");
        File.WriteAllText(Path.Combine(project, "storage", "framework", "CACHE", "item"), "excluded");
        var linkedDirectory = Path.Combine(project, "linked-external");
        var reparsePointCreated = false;
        try
        {
            Directory.CreateSymbolicLink(linkedDirectory, Path.Combine(project, "app"));
            reparsePointCreated = true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException)
        {
        }
        var dump = Path.Combine(root, "database.sql");
        File.WriteAllText(dump, "CREATE TABLE example (id INT);");
        var output = Path.Combine(root, "portable.zip");

        try
        {
            await ThrowsAsync<InvalidOperationException>(
                () => SiteWorkflowArchive.CreateAsync(
                    project,
                    Path.Combine(project, "inside.zip"),
                    "Contract Site"
                ),
                "portable project exports reject destinations inside the project"
            );

            await SiteWorkflowArchive.CreateAsync(project, output, "Contract Site", dump);
            using var archive = ZipFile.OpenRead(output);
            var entries = archive.Entries.Select(entry => entry.FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Check(
                entries.Contains("app/index.php")
                    && entries.Contains(".env")
                    && entries.Contains("database/database.sql")
                    && entries.Contains("herdme-export.json"),
                "portable project exports include source, environment, database, and manifest files"
            );
            Check(
                !entries.Any(entry => entry.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
                    || entry.StartsWith("vendor/", StringComparison.OrdinalIgnoreCase)
                    || entry.StartsWith("node_modules/", StringComparison.OrdinalIgnoreCase)
                    || entry.StartsWith("storage/logs/", StringComparison.OrdinalIgnoreCase)
                    || entry.StartsWith("storage/framework/cache/", StringComparison.OrdinalIgnoreCase)
                    || reparsePointCreated
                        && entry.StartsWith("linked-external/", StringComparison.OrdinalIgnoreCase)),
                "portable project exports omit repositories, dependencies, logs, and generated caches"
            );
            using var manifest = JsonDocument.Parse(
                archive.GetEntry("herdme-export.json")!.Open()
            );
            Check(
                manifest.RootElement.GetProperty("site").GetString() == "Contract Site"
                    && manifest.RootElement.GetProperty("databaseIncluded").GetBoolean(),
                "portable project export manifests describe the site and bundled database"
            );
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static IReadOnlyDictionary<string, string> NpmFixtureEnvironment(string mode)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HERDME_NPM_RUNNER_FIXTURE"] = mode
        };
    }

}
