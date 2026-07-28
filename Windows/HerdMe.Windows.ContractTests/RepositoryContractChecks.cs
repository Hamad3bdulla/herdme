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
                    "mariadb", "mysql", "postgresql", "mongodb", "redis",
                    "valkey", "meilisearch", "typesense", "minio", "rustfs"
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
                ),
            "Laravel project creation reuses the application-owned runtime services"
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
                ),
            "first-run setup reuses the application-owned settings and runtime services"
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
                @"new\s+(CoreClient|SiteConfigurationStore|PhpRuntimeInstaller|PhpRuntimePolicy|ComposerToolManager|NodeRuntimeInstaller|WindowsLocalEnvironment|WindowsServiceManager)\s*\("
            )),
            "Windows pages do not construct duplicate application services"
        );

        var mainWindowSource = File.ReadAllText(Path.Combine(windowsRoot, "MainWindow.xaml.cs"));
        Check(
            mainWindowSource.Contains("public MainWindow(AppServices services", StringComparison.Ordinal)
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
                && appSource.Contains("WriteUnhandledExceptionAsync", StringComparison.Ordinal),
            "Windows application failures are routed exclusively through structured diagnostics"
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
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
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
                        codeBehind,
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
                && appCodeBehind.Contains("new GeneratedIconSource", StringComparison.Ordinal)
                && appCodeBehind.Contains(
                    "global::Windows.UI.Color.FromArgb",
                    StringComparison.Ordinal
                ),
            "the Windows tray icon is created with the H.NotifyIcon generated icon source API"
        );
        Check(
            !appCodeBehind.Contains("Resources[\"OpenHerdMeCommand\"]", StringComparison.Ordinal)
                && !appCodeBehind.Contains("Resources[\"QuitHerdMeCommand\"]", StringComparison.Ordinal)
                && !appCodeBehind.Contains("Resources[\"StartAllCommand\"]", StringComparison.Ordinal)
                && !appCodeBehind.Contains("Resources[\"StopAllCommand\"]", StringComparison.Ordinal)
                && appCodeBehind.Contains("new XamlUICommand", StringComparison.Ordinal)
                && appCodeBehind.Contains(
                    "AppLocalization.Get(\"TrayOpenCommand.Label\")",
                    StringComparison.Ordinal
                )
                && appCodeBehind.Contains(
                    "AppLocalization.Get(\"TrayQuitCommand.Label\")",
                    StringComparison.Ordinal
                )
                && appCodeBehind.Contains(
                    "AppLocalization.Get(\"TrayStartAllCommand.Label\")",
                    StringComparison.Ordinal
                )
                && appCodeBehind.Contains(
                    "AppLocalization.Get(\"TrayStopAllCommand.Label\")",
                    StringComparison.Ordinal
                ),
            "the Windows tray commands are constructed from localized strings at runtime"
        );

        var projectDocument = XDocument.Load(
            Path.Combine(projectRoot, "HerdMe.Windows.csproj")
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
                && buildTools.Contains("System.Security.Permissions.dll", StringComparison.Ordinal),
            "the Windows build locates Visual Studio MSBuild and patches its XAML task dependency"
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
                ),
            "the portable Windows publish uses the validated Visual Studio MSBuild path"
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
            "OnboardingSetupSummary.Text",
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
        Check(
            sitesPageSource.Contains("DisplayOption", StringComparison.Ordinal)
                && sitesPageSource.Contains("selectedPreset.Value", StringComparison.Ordinal)
                && sitesPageSource.Contains("siteRuntimeStore.SetPhp", StringComparison.Ordinal),
            "Windows Sites keeps localized labels separate from runtime and command values"
        );
        var sitesPageXaml = File.ReadAllText(
            Path.Combine(projectRoot, "Pages", "SitesPage.xaml")
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

    private static IReadOnlyDictionary<string, string> NpmFixtureEnvironment(string mode)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HERDME_NPM_RUNNER_FIXTURE"] = mode
        };
    }

}
