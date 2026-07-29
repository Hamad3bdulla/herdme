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
using static ContractChecks;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

var npmRunnerFixture = Environment.GetEnvironmentVariable("HERDME_NPM_RUNNER_FIXTURE");
if (Environment.GetEnvironmentVariable("HERDME_CORE_CLIENT_FAILURE_FIXTURE") == "1")
{
    Environment.ExitCode = 42;
    return;
}
if (npmRunnerFixture == "arguments")
{
    Console.Write(JsonSerializer.Serialize(args));
    return;
}
if (npmRunnerFixture == "delay")
{
    await Task.Delay(TimeSpan.FromSeconds(30));
    return;
}

if (args.SequenceEqual(["-m"], StringComparer.Ordinal))
{
    Console.WriteLine("[PHP Modules]");
    foreach (var module in new[]
    {
        "ctype", "curl", "dom", "fileinfo", "filter", "hash", "mbstring",
        "openssl", "pcre", "pdo", "session", "tokenizer", "xml", "zip"
    })
    {
        Console.WriteLine(module);
    }
    return;
}

var verifyLiveServices = args.Contains("--live-service-releases", StringComparer.Ordinal);
var verifyLiveRuntimes = args.Contains("--live-runtime-releases", StringComparer.Ordinal);
var verifyLiveGitInstall = args.Contains("--live-git-install", StringComparer.Ordinal);
var verifyLiveManagedCommandPath = args.Contains(
    "--live-managed-command-path",
    StringComparer.Ordinal
);
if (verifyLiveServices || verifyLiveRuntimes || verifyLiveGitInstall
    || verifyLiveManagedCommandPath)
{
    if (verifyLiveServices) await VerifyLiveServiceReleasesAsync();
    if (verifyLiveRuntimes) await VerifyLiveRuntimeReleasesAsync();
    if (verifyLiveGitInstall) await VerifyLiveGitInstallAsync();
    if (verifyLiveManagedCommandPath) await VerifyLiveManagedCommandPathAsync();
    Console.WriteLine("HerdMe Windows live checks passed");
    return;
}

var repositoryRoot = FindRepositoryRoot();
VerifyReleaseAndInstallerContracts(repositoryRoot);
VerifyXamlContracts(repositoryRoot);
VerifyCompositionContracts(repositoryRoot);
VerifyLocalizationContracts(repositoryRoot);
VerifyArtisanCommandContracts(repositoryRoot);
await VerifyNpmScriptContractsAsync(repositoryRoot);
VerifyRuntimeCatalogContracts(repositoryRoot);

var supportRoot = Path.Combine(
    Path.GetTempPath(),
    "herdme-windows-contracts-" + Guid.NewGuid().ToString("N")
);
try
{
    await VerifyDownloadAndStorageContractsAsync(supportRoot);

    VerifySiteContracts(supportRoot);

    await VerifyServiceContractsAsync(supportRoot);

    await VerifyToolAndUpdateContractsAsync(supportRoot);
}
finally
{
    if (Directory.Exists(supportRoot)) Directory.Delete(supportRoot, true);
}

Console.WriteLine("HerdMe Windows cross-platform contract tests passed");
