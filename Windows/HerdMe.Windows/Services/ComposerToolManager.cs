using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed partial class ComposerToolManager
{
    private static readonly HttpClient HttpClient = ManagedDownloadClient.Create();
    private readonly CoreClient coreClient;
    private readonly PhpRuntimeInstaller phpInstaller;
    private readonly PhpRuntimePolicy phpPolicy;
    private readonly NodeRuntimeInstaller nodeInstaller;

    public ComposerToolManager(
        string? supportRoot = null,
        CoreClient? coreClient = null,
        PhpRuntimeInstaller? phpInstaller = null,
        PhpRuntimePolicy? phpPolicy = null,
        NodeRuntimeInstaller? nodeInstaller = null
    )
    {
        SupportRoot = supportRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
        this.coreClient = coreClient ?? new CoreClient();
        this.phpInstaller = phpInstaller ?? new PhpRuntimeInstaller(this.coreClient, SupportRoot);
        this.phpPolicy = phpPolicy ?? new PhpRuntimePolicy(this.coreClient);
        this.nodeInstaller = nodeInstaller ?? new NodeRuntimeInstaller(SupportRoot);
    }

    public string SupportRoot { get; }

    public string ComposerPath => Path.Combine(SupportRoot, "bin", "composer.phar");

    public string ComposerCommandPath => Path.Combine(SupportRoot, "bin", "composer.cmd");

    public string ComposerHome => Path.Combine(SupportRoot, "Composer");

    public string LaravelExecutable => Path.Combine(
        ComposerHome,
        "vendor",
        "laravel",
        "installer",
        "bin",
        "laravel"
    );

    public async Task<ComposerRelease> ResolveComposerReleaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        using var stream = await HttpClient.GetStreamAsync(
            "https://getcomposer.org/versions",
            cancellationToken
        );
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var stable = document.RootElement.GetProperty("stable").EnumerateArray().FirstOrDefault();
        var version = stable.GetProperty("version").GetString();
        var path = stable.GetProperty("path").GetString();
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(path)
            || !path.StartsWith("/download/", StringComparison.Ordinal)
            || !path.EndsWith("/composer.phar", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Composer's stable release metadata was invalid.");
        }
        var checksumText = await HttpClient.GetStringAsync(
            "https://getcomposer.org" + path + ".sha256sum",
            cancellationToken
        );
        var checksum = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (checksum is not { Length: 64 } || !checksum.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Composer's official SHA-256 metadata was invalid.");
        }
        return new ComposerRelease(
            version,
            checksum,
            new Uri("https://getcomposer.org" + path)
        );
    }

    public async Task<(string Composer, string Laravel)> InstallOrUpdateAsync(
        string phpCycle,
        CancellationToken cancellationToken = default
    )
    {
        var php = await PreparePhpAsync(phpCycle, cancellationToken);
        var release = await ResolveComposerReleaseAsync(cancellationToken);
        var binDirectory = Path.GetDirectoryName(ComposerPath)!;
        var cacheDirectory = Path.Combine(SupportRoot, "Cache", "composer");
        Directory.CreateDirectory(binDirectory);
        Directory.CreateDirectory(cacheDirectory);
        Directory.CreateDirectory(ComposerHome);
        var temporary = Path.Combine(cacheDirectory, $"composer-{Guid.NewGuid():N}.phar");
        try
        {
            await DownloadAndVerifyAsync(release, temporary, cancellationToken);
            var versionOutput = await RunAsync(
                php,
                [temporary, "--version", "--no-ansi"],
                SupportRoot,
                ManagedEnvironment(phpCycle),
                cancellationToken
            );
            var composerVersion = ExtractVersion(versionOutput)
                ?? throw new InvalidDataException("The verified Composer PHAR did not report a version.");
            File.Move(temporary, ComposerPath, true);
            EnsureComposerCommand(phpCycle);

            await RunAsync(
                php,
                [
                    ComposerPath,
                    "global",
                    "require",
                    "laravel/installer:^5",
                    "--no-interaction",
                    "--no-progress",
                    "--no-ansi"
                ],
                SupportRoot,
                ManagedEnvironment(phpCycle),
                cancellationToken
            );
            var laravelVersion = await LaravelInstallerVersionAsync(phpCycle, cancellationToken)
                ?? throw new InvalidDataException("Laravel Installer was unavailable after Composer finished.");
            return (composerVersion, laravelVersion);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<(string? Composer, string? Laravel)> InstalledVersionsAsync(
        string phpCycle,
        CancellationToken cancellationToken = default
    )
    {
        return (
            await ComposerVersionAsync(phpCycle, cancellationToken),
            await LaravelInstallerVersionAsync(phpCycle, cancellationToken)
        );
    }

    public async Task<string?> ComposerVersionAsync(
        string phpCycle,
        CancellationToken cancellationToken = default
    )
    {
        if (!phpInstaller.IsInstalled(phpCycle) || !File.Exists(ComposerPath)) return null;
        try
        {
            return ExtractVersion(await RunAsync(
                phpInstaller.PhpExecutable(phpCycle),
                [ComposerPath, "--version", "--no-ansi"],
                SupportRoot,
                ManagedEnvironment(phpCycle),
                cancellationToken
            ));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<string?> LaravelInstallerVersionAsync(
        string phpCycle,
        CancellationToken cancellationToken = default
    )
    {
        if (!phpInstaller.IsInstalled(phpCycle) || !File.Exists(LaravelExecutable)) return null;
        try
        {
            var output = await RunAsync(
                phpInstaller.PhpExecutable(phpCycle),
                [LaravelExecutable, "--version", "--no-ansi"],
                SupportRoot,
                ManagedEnvironment(phpCycle),
                cancellationToken
            );
            return ExtractVersion(output);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<string> LatestLaravelInstallerVersionAsync(
        CancellationToken cancellationToken = default
    )
    {
        using var stream = await HttpClient.GetStreamAsync(
            "https://repo.packagist.org/p2/laravel/installer.json",
            cancellationToken
        );
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        foreach (var release in document.RootElement
            .GetProperty("packages")
            .GetProperty("laravel/installer")
            .EnumerateArray())
        {
            var version = release.GetProperty("version").GetString();
            if (version is not null && Version.TryParse(RuntimeVersionComparison.Normalize(version), out _))
            {
                return RuntimeVersionComparison.Normalize(version);
            }
        }
        throw new InvalidDataException("Packagist did not return a stable Laravel Installer release.");
    }

    public async Task EnsureLaravelInstallerAsync(
        string phpCycle,
        CancellationToken cancellationToken = default
    )
    {
        if (IsLaravelInstallerReady(phpCycle))
        {
            EnsureComposerCommand(phpCycle);
            return;
        }
        await InstallOrUpdateAsync(phpCycle, cancellationToken);
    }

    internal void EnsureComposerCommand(string phpCycle)
    {
        if (!PhpRuntimeInstaller.IsSupportedCycle(phpCycle))
        {
            throw new ArgumentOutOfRangeException(
                nameof(phpCycle),
                phpCycle,
                "The Composer command requires a supported HerdMe PHP cycle."
            );
        }
        if (!File.Exists(ComposerPath))
        {
            throw new FileNotFoundException("The managed Composer PHAR is missing.", ComposerPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ComposerCommandPath)!);
        var content = string.Join("\r\n", [
            "@echo off",
            $"\"%~dp0..\\Runtimes\\php\\{phpCycle}\\php.exe\" \"%~dp0composer.phar\" %*",
            string.Empty
        ]);
        if (File.Exists(ComposerCommandPath)
            && File.ReadAllText(ComposerCommandPath).Equals(content, StringComparison.Ordinal))
        {
            return;
        }
        File.WriteAllText(
            ComposerCommandPath,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
    }

    public bool IsLaravelInstallerReady(string phpCycle)
    {
        return phpInstaller.IsInstalled(phpCycle)
            && File.Exists(ComposerPath)
            && File.Exists(LaravelExecutable);
    }

    public IReadOnlyDictionary<string, string> ManagedEnvironment(string phpCycle)
    {
        var paths = CommandLineDirectories(phpCycle).ToList();
        paths.Add(Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["COMPOSER_HOME"] = ComposerHome,
            ["COMPOSER_CACHE_DIR"] = Path.Combine(SupportRoot, "Cache", "composer"),
            ["COMPOSER_NO_INTERACTION"] = "1",
            ["PATH"] = string.Join(Path.PathSeparator, paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        };
    }

    public IReadOnlyList<string> CommandLineDirectories(string phpCycle)
    {
        var paths = new List<string>();
        var phpDirectory = Path.GetDirectoryName(phpInstaller.PhpExecutable(phpCycle))!;
        if (File.Exists(phpInstaller.PhpExecutable(phpCycle))) paths.Add(phpDirectory);
        var binDirectory = Path.Combine(SupportRoot, "bin");
        if (Directory.Exists(binDirectory)) paths.Add(binDirectory);
        var composerBin = Path.Combine(ComposerHome, "vendor", "bin");
        if (Directory.Exists(composerBin)) paths.Add(composerBin);
        var activeNode = nodeInstaller.LoadSettings().ActiveVersion;
        if (!string.IsNullOrWhiteSpace(activeNode))
        {
            var nodeDirectory = Path.Combine(nodeInstaller.RuntimeRoot, activeNode);
            if (File.Exists(Path.Combine(nodeDirectory, "node.exe"))) paths.Add(nodeDirectory);
        }
        var git = new GitRuntimeInstaller(SupportRoot).InstalledExecutable();
        if (git is not null) paths.Add(Path.GetDirectoryName(git)!);
        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<string> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken = default
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var variable in environment) startInfo.Environment[variable.Key] = variable.Value;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{Path.GetFileName(executable)} could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The command exited between the cancellation check and tree termination.
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(standardOutput, standardError);
            throw;
        }
        var output = (await standardOutput) + Environment.NewLine + (await standardError);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output)
                ? $"{Path.GetFileName(executable)} failed with exit code {process.ExitCode}."
                : output.Trim());
        }
        return output.Trim();
    }

    private async Task<string> PreparePhpAsync(string cycle, CancellationToken cancellationToken)
    {
        if (!phpInstaller.IsInstalled(cycle))
        {
            throw new InvalidOperationException($"Install HerdMe PHP {cycle} before installing Composer.");
        }
        await phpInstaller.EnsureManagedConfigurationAsync(cycle, cancellationToken);
        var php = phpInstaller.PhpExecutable(cycle);
        await phpPolicy.PrepareLaunchAsync(php, cancellationToken);
        return php;
    }

    private static async Task DownloadAndVerifyAsync(
        ComposerRelease release,
        string destination,
        CancellationToken cancellationToken
    )
    {
        await using var source = await HttpClient.GetStreamAsync(release.DownloadUri, cancellationToken);
        await using var output = File.Create(destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1_024];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            hash.AppendData(buffer, 0, count);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Composer failed its official SHA-256 verification.");
        }
    }

    private static string? ExtractVersion(string output)
    {
        return VersionPattern().Match(output) is { Success: true } match ? match.Value : null;
    }

    [GeneratedRegex(@"\b[0-9]+\.[0-9]+\.[0-9]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
