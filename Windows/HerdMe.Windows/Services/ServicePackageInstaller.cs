using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public sealed class ServicePackageInstaller
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ServicePackageInstaller(string? supportRoot = null)
    {
        SupportRoot = supportRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
    }

    public string SupportRoot { get; }

    public string RuntimeRoot => Path.Combine(SupportRoot, "Runtimes", "services");

    public bool IsInstalled(string definitionId) => File.Exists(ExecutablePath(definitionId));

    public string ExecutablePath(string definitionId)
    {
        var runtime = Path.Combine(RuntimeRoot, definitionId);
        return Path.Combine(runtime, RelativeExecutablePath(definitionId));
    }

    public string? InstalledVersion(string definitionId)
    {
        if (!IsInstalled(definitionId)) return null;
        var manifestPath = Path.Combine(RuntimeRoot, definitionId, "herdme-runtime.json");
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return manifest.RootElement.GetProperty("version").GetString();
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException)
        {
            return "Installed";
        }
    }

    public Task<ServicePackageRelease> ResolveReleaseAsync(
        string definitionId,
        CancellationToken cancellationToken = default
    )
    {
        EnsureInstallable(definitionId);
        return definitionId switch
        {
            "mariadb" => ResolveMariaDbReleaseAsync(cancellationToken),
            "mysql" => ResolveMySqlReleaseAsync(cancellationToken),
            "postgresql" => ResolvePostgreSqlReleaseAsync(cancellationToken),
            "mongodb" => ResolveMongoDbReleaseAsync(cancellationToken),
            "redis" => ResolveRedisReleaseAsync(cancellationToken),
            "meilisearch" => ResolveMeilisearchReleaseAsync(cancellationToken),
            "minio" => ResolveMinioReleaseAsync(cancellationToken),
            "rustfs" => ResolveRustFsReleaseAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(definitionId), definitionId, "Unsupported managed service."
            )
        };
    }

    public async Task<ServicePackageRelease> InstallAsync(
        string definitionId,
        CancellationToken cancellationToken = default
    )
    {
        EnsureInstallable(definitionId);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Managed Windows services can only be installed on Windows.");
        }

        var release = await ResolveReleaseAsync(definitionId, cancellationToken);
        Directory.CreateDirectory(RuntimeRoot);
        var cache = Path.Combine(SupportRoot, "Cache", "services");
        Directory.CreateDirectory(cache);
        var download = Path.Combine(cache, $"{Guid.NewGuid():N}-{release.FileName}");
        var stagingContainer = Path.Combine(RuntimeRoot, $".install-{Guid.NewGuid():N}");
        var stagingRuntime = Path.Combine(stagingContainer, definitionId);
        var destination = Path.Combine(RuntimeRoot, definitionId);
        var backup = Path.Combine(RuntimeRoot, $".backup-{Guid.NewGuid():N}");

        try
        {
            await DownloadAndVerifyAsync(release, download, cancellationToken);
            Directory.CreateDirectory(stagingContainer);
            if (release.IsZipArchive)
            {
                ZipFile.ExtractToDirectory(download, stagingContainer);
                NormalizeExtractedRuntime(stagingContainer, definitionId);
            }
            else
            {
                Directory.CreateDirectory(stagingRuntime);
                File.Move(download, Path.Combine(stagingRuntime, release.FileName));
            }

            if (!File.Exists(ExpectedExecutable(stagingRuntime, definitionId)))
            {
                throw new InvalidDataException($"The official {definitionId} package did not contain its server executable.");
            }
            await File.WriteAllTextAsync(
                Path.Combine(stagingRuntime, "herdme-runtime.json"),
                JsonSerializer.Serialize(new
                {
                    service = release.DefinitionId,
                    version = release.Version,
                    source = release.DownloadUri.ToString(),
                    checksumAlgorithm = release.ChecksumAlgorithm.ToString().ToLowerInvariant(),
                    checksum = release.Checksum
                }, JsonOptions),
                cancellationToken
            );

            if (Directory.Exists(destination)) Directory.Move(destination, backup);
            try
            {
                Directory.Move(stagingRuntime, destination);
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
            }
            catch
            {
                if (!Directory.Exists(destination) && Directory.Exists(backup))
                {
                    Directory.Move(backup, destination);
                }
                throw;
            }
            return release;
        }
        finally
        {
            if (File.Exists(download)) File.Delete(download);
            if (Directory.Exists(stagingContainer)) Directory.Delete(stagingContainer, true);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
        }
    }

    private static void EnsureInstallable(string definitionId)
    {
        var definition = ManagedServiceCatalog.Get(definitionId);
        if (definition.IsInstallable) return;

        throw new NotSupportedException(
            definition.UnavailableReason
                ?? $"{definition.Name} is not available for native Windows installation."
        );
    }

    public async Task<ServicePackageRelease> ResolveMariaDbReleaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        using var stream = await HttpClient.GetStreamAsync(
            "https://downloads.mariadb.org/rest-api/mariadb/11.8/",
            cancellationToken
        );
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var candidates = new List<(Version Parsed, ServicePackageRelease Release)>();
        foreach (var property in document.RootElement.GetProperty("releases").EnumerateObject())
        {
            var release = property.Value;
            var releaseName = release.GetProperty("release_name").GetString() ?? string.Empty;
            if (releaseName.Contains("Preview", StringComparison.OrdinalIgnoreCase)
                || releaseName.Contains("RC", StringComparison.OrdinalIgnoreCase)
                || releaseName.Contains("Beta", StringComparison.OrdinalIgnoreCase)
                || !Version.TryParse(property.Name, out var parsed))
            {
                continue;
            }
            foreach (var file in release.GetProperty("files").EnumerateArray())
            {
                var fileName = file.GetProperty("file_name").GetString();
                if (fileName is null
                    || !fileName.Equals($"mariadb-{property.Name}-winx64.zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var checksum = file.GetProperty("checksum").GetProperty("sha256sum").GetString();
                var download = file.GetProperty("file_download_url").GetString();
                if (!IsSha256(checksum) || !Uri.TryCreate(download, UriKind.Absolute, out var uri)) continue;
                var secureUri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
                candidates.Add((parsed, new ServicePackageRelease(
                    "mariadb",
                    property.Name,
                    fileName,
                    ServicePackageChecksumAlgorithm.Sha256,
                    checksum!,
                    secureUri,
                    true
                )));
            }
        }
        return candidates.OrderByDescending(candidate => candidate.Parsed)
            .Select(candidate => candidate.Release)
            .FirstOrDefault()
            ?? throw new InvalidDataException("MariaDB did not publish a stable Windows x64 ZIP with SHA-256 metadata.");
    }

    public async Task<ServicePackageRelease> ResolveMeilisearchReleaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        using var stream = await HttpClient.GetStreamAsync(
            "https://api.github.com/repos/meilisearch/meilisearch/releases/latest",
            cancellationToken
        );
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var version = (document.RootElement.GetProperty("tag_name").GetString() ?? string.Empty).TrimStart('v');
        foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
        {
            const string fileName = "meilisearch-windows-amd64.exe";
            if (!fileName.Equals(asset.GetProperty("name").GetString(), StringComparison.Ordinal)) continue;
            var digest = asset.GetProperty("digest").GetString();
            var checksum = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
                ? digest[7..]
                : null;
            var download = asset.GetProperty("browser_download_url").GetString();
            if (string.IsNullOrWhiteSpace(version) || !IsSha256(checksum)
                || !Uri.TryCreate(download, UriKind.Absolute, out var uri))
            {
                break;
            }
            return new ServicePackageRelease(
                "meilisearch",
                version,
                "meilisearch.exe",
                ServicePackageChecksumAlgorithm.Sha256,
                checksum!,
                uri,
                false
            );
        }
        throw new InvalidDataException("Meilisearch did not publish an open-source Windows x64 asset with a SHA-256 digest.");
    }

    public async Task<ServicePackageRelease> ResolveMySqlReleaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://dev.mysql.com/downloads/mysql/?tpl=files&os=3"
        );
        // Oracle rejects product-specific agents here while allowing command-line clients.
        request.Headers.UserAgent.ParseAdd("curl/8.7.1");
        request.Headers.UserAgent.ParseAdd("HerdMe/1.0");
        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        response.EnsureSuccessStatusCode();
        var metadata = await response.Content.ReadAsStringAsync(cancellationToken);
        return SelectMySqlRelease(metadata);
    }

    public static ServicePackageRelease SelectMySqlRelease(string metadata)
    {
        const string pattern = @"\(mysql-(?<version>\d+\.\d+\.\d+)-winx64\.zip\).*?"
            + @"MD5:\s*<code\s+class=""md5"">(?<checksum>[0-9a-f]{32})</code>";
        var candidates = new List<(Version Parsed, ServicePackageRelease Release)>();
        foreach (Match match in Regex.Matches(
                     metadata,
                     pattern,
                     RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
                 ))
        {
            var version = match.Groups["version"].Value;
            var checksum = match.Groups["checksum"].Value;
            if (!Version.TryParse(version, out var parsed) || !IsMd5(checksum)) continue;
            var fileName = $"mysql-{version}-winx64.zip";
            var uri = new Uri(
                $"https://cdn.mysql.com/Downloads/MySQL-{parsed.Major}.{parsed.Minor}/{fileName}"
            );
            candidates.Add((parsed, new ServicePackageRelease(
                "mysql",
                version,
                fileName,
                ServicePackageChecksumAlgorithm.Md5,
                checksum,
                uri,
                true
            )));
        }
        return candidates.OrderByDescending(candidate => candidate.Parsed)
            .Select(candidate => candidate.Release)
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                "MySQL did not publish a stable Windows x64 ZIP with its vendor checksum."
            );
    }

    public static Task<ServicePackageRelease> ResolvePostgreSqlReleaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ServicePackageRelease(
            "postgresql",
            "18.4",
            "postgresql-18.4-2-windows-x64-binaries.zip",
            ServicePackageChecksumAlgorithm.Sha256,
            "02e239529ed7833d169f98d915d3feffe0813264b08b3ae353e78e8b9c97e1a6",
            new Uri(
                "https://get.enterprisedb.com/postgresql/postgresql-18.4-2-windows-x64-binaries.zip"
            ),
            true
        ));
    }

    public async Task<ServicePackageRelease> ResolveMongoDbReleaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        var metadata = await HttpClient.GetStringAsync(
            "https://downloads.mongodb.org/current.json",
            cancellationToken
        );
        return SelectMongoDbRelease(metadata);
    }

    public static ServicePackageRelease SelectMongoDbRelease(string metadata)
    {
        using var document = JsonDocument.Parse(metadata);
        var candidates = new List<(Version Parsed, ServicePackageRelease Release)>();
        foreach (var versionEntry in document.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (versionEntry.TryGetProperty("development_release", out var development)
                && development.GetBoolean())
            {
                continue;
            }
            var versionText = versionEntry.GetProperty("version").GetString() ?? string.Empty;
            if (!Version.TryParse(versionText, out var parsed) || parsed.Major != 8 || parsed.Minor != 0)
            {
                continue;
            }
            foreach (var download in versionEntry.GetProperty("downloads").EnumerateArray())
            {
                if (download.GetProperty("arch").GetString() != "x86_64"
                    || download.GetProperty("target").GetString() != "windows"
                    || download.GetProperty("edition").GetString() != "base")
                {
                    continue;
                }
                var archive = download.GetProperty("archive");
                var checksum = archive.GetProperty("sha256").GetString();
                var downloadValue = archive.GetProperty("url").GetString();
                if (!IsSha256(checksum)
                    || !Uri.TryCreate(downloadValue, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps
                    || !uri.Host.Equals("fastdl.mongodb.org", StringComparison.OrdinalIgnoreCase)
                    || !uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                candidates.Add((parsed, new ServicePackageRelease(
                    "mongodb",
                    versionText,
                    Path.GetFileName(uri.AbsolutePath),
                    ServicePackageChecksumAlgorithm.Sha256,
                    checksum!,
                    uri,
                    true
                )));
            }
        }
        return candidates.OrderByDescending(candidate => candidate.Parsed)
            .Select(candidate => candidate.Release)
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                "MongoDB did not publish an 8.0 LTS Community Server ZIP for Windows x64 with SHA-256 metadata."
            );
    }

    public async Task<ServicePackageRelease> ResolveRedisReleaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        var metadata = await HttpClient.GetStringAsync(
            "https://api.github.com/repos/redis-windows/redis-windows/releases/latest",
            cancellationToken
        );
        return SelectRedisRelease(metadata);
    }

    public static ServicePackageRelease SelectRedisRelease(string metadata)
    {
        using var document = JsonDocument.Parse(metadata);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
        {
            throw new InvalidDataException("The latest Redis for Windows release is still a draft.");
        }
        var version = (root.GetProperty("tag_name").GetString() ?? string.Empty).TrimStart('v');
        if (!Version.TryParse(version, out _))
        {
            throw new InvalidDataException("Redis for Windows returned an invalid release version.");
        }
        var expectedName = $"Redis-{version}-Windows-x64-msys2.zip";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!expectedName.Equals(asset.GetProperty("name").GetString(), StringComparison.Ordinal))
            {
                continue;
            }
            var digest = asset.GetProperty("digest").GetString();
            var checksum = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
                ? digest[7..]
                : null;
            var download = asset.GetProperty("browser_download_url").GetString();
            if (!IsSha256(checksum)
                || !Uri.TryCreate(download, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.StartsWith(
                    "/redis-windows/redis-windows/releases/download/",
                    StringComparison.Ordinal
                ))
            {
                continue;
            }
            return new ServicePackageRelease(
                "redis",
                version,
                expectedName,
                ServicePackageChecksumAlgorithm.Sha256,
                checksum!,
                uri,
                true
            );
        }
        throw new InvalidDataException(
            "Redis for Windows did not publish an x64 MSYS2 ZIP with a SHA-256 digest."
        );
    }

    public async Task<ServicePackageRelease> ResolveMinioReleaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        var checksumText = await HttpClient.GetStringAsync(
            "https://dl.min.io/server/minio/release/windows-amd64/minio.exe.sha256sum",
            cancellationToken
        );
        var parts = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !IsSha256(parts[0])
            || !parts[1].StartsWith("minio.RELEASE.", StringComparison.Ordinal)
            || parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("MinIO's official Windows checksum metadata was invalid.");
        }
        var version = parts[1]["minio.RELEASE.".Length..];
        return new ServicePackageRelease(
            "minio",
            version,
            "minio.exe",
            ServicePackageChecksumAlgorithm.Sha256,
            parts[0],
            new Uri($"https://dl.min.io/server/minio/release/windows-amd64/archive/{parts[1]}"),
            false
        );
    }

    public async Task<ServicePackageRelease> ResolveRustFsReleaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        var metadata = await HttpClient.GetStringAsync(
            "https://api.github.com/repos/rustfs/rustfs/releases?per_page=20",
            cancellationToken
        );
        return SelectRustFsRelease(metadata);
    }

    public static ServicePackageRelease SelectRustFsRelease(string metadata)
    {
        using var document = JsonDocument.Parse(metadata);
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            var tag = (release.GetProperty("tag_name").GetString() ?? string.Empty).Trim();
            var version = tag.TrimStart('v');
            if (version.Length == 0) continue;
            var expectedName = $"rustfs-windows-x86_64-v{version}.zip";
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (!expectedName.Equals(asset.GetProperty("name").GetString(), StringComparison.Ordinal))
                {
                    continue;
                }
                var digest = asset.GetProperty("digest").GetString();
                var checksum = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
                    ? digest[7..]
                    : null;
                var download = asset.GetProperty("browser_download_url").GetString();
                if (!IsSha256(checksum)
                    || !Uri.TryCreate(download, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps
                    || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    || !uri.AbsolutePath.StartsWith(
                        "/rustfs/rustfs/releases/download/",
                        StringComparison.Ordinal
                    ))
                {
                    continue;
                }
                return new ServicePackageRelease(
                    "rustfs",
                    version,
                    expectedName,
                    ServicePackageChecksumAlgorithm.Sha256,
                    checksum!,
                    uri,
                    true
                );
            }
        }
        throw new InvalidDataException(
            "RustFS did not publish an official Windows x64 ZIP with a SHA-256 digest."
        );
    }

    private static async Task DownloadAndVerifyAsync(
        ServicePackageRelease release,
        string destination,
        CancellationToken cancellationToken
    )
    {
        await using var source = await HttpClient.GetStreamAsync(release.DownloadUri, cancellationToken);
        await using var output = File.Create(destination);
        var algorithm = release.ChecksumAlgorithm switch
        {
            ServicePackageChecksumAlgorithm.Sha256 => HashAlgorithmName.SHA256,
            ServicePackageChecksumAlgorithm.Md5 => HashAlgorithmName.MD5,
            _ => throw new InvalidDataException("The service package uses an unsupported checksum algorithm.")
        };
        using var hash = IncrementalHash.CreateHash(algorithm);
        var buffer = new byte[128 * 1_024];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            hash.AppendData(buffer, 0, count);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!actual.Equals(release.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The {release.DefinitionId} package failed {release.ChecksumAlgorithm} verification."
            );
        }
    }

    internal static string NormalizeExtractedRuntime(string stagingContainer, string definitionId)
    {
        var stagingRuntime = Path.Combine(stagingContainer, definitionId);
        var extracted = FindExtractedRuntime(stagingContainer, definitionId);
        if (Path.GetFullPath(extracted).Equals(
                Path.GetFullPath(stagingRuntime),
                StringComparison.OrdinalIgnoreCase
            ))
        {
            return stagingRuntime;
        }
        if (!Path.GetFullPath(extracted).Equals(
                Path.GetFullPath(stagingContainer),
                StringComparison.OrdinalIgnoreCase
            ))
        {
            Directory.Move(extracted, stagingRuntime);
            return stagingRuntime;
        }

        Directory.CreateDirectory(stagingRuntime);
        foreach (var file in Directory.EnumerateFiles(stagingContainer, "*", SearchOption.TopDirectoryOnly))
        {
            File.Move(file, Path.Combine(stagingRuntime, Path.GetFileName(file)));
        }
        foreach (var directory in Directory.EnumerateDirectories(
                     stagingContainer,
                     "*",
                     SearchOption.TopDirectoryOnly
                 ).Where(directory => !Path.GetFullPath(directory).Equals(
                     Path.GetFullPath(stagingRuntime),
                     StringComparison.OrdinalIgnoreCase
                 )).ToList())
        {
            Directory.Move(directory, Path.Combine(stagingRuntime, Path.GetFileName(directory)));
        }
        return stagingRuntime;
    }

    private static string FindExtractedRuntime(string stagingContainer, string definitionId)
    {
        if (File.Exists(ExpectedExecutable(stagingContainer, definitionId))) return stagingContainer;
        return Directory.EnumerateDirectories(stagingContainer, "*", SearchOption.AllDirectories)
            .FirstOrDefault(directory => File.Exists(ExpectedExecutable(directory, definitionId)))
            ?? throw new InvalidDataException($"The official {definitionId} ZIP layout was invalid.");
    }

    private static string ExpectedExecutable(string runtime, string definitionId)
    {
        return Path.Combine(runtime, RelativeExecutablePath(definitionId));
    }

    private static string RelativeExecutablePath(string definitionId)
    {
        return definitionId switch
        {
            "mariadb" => Path.Combine("bin", "mariadbd.exe"),
            "mysql" => Path.Combine("bin", "mysqld.exe"),
            "postgresql" => Path.Combine("bin", "postgres.exe"),
            "mongodb" => Path.Combine("bin", "mongod.exe"),
            "redis" => "redis-server.exe",
            "meilisearch" => "meilisearch.exe",
            "minio" => "minio.exe",
            "rustfs" => "rustfs.exe",
            _ => throw new ArgumentOutOfRangeException(
                nameof(definitionId), definitionId, "Unsupported managed service."
            )
        };
    }

    private static bool IsSha256(string? value)
    {
        return value is { Length: 64 } && value.All(Uri.IsHexDigit);
    }

    private static bool IsMd5(string? value)
    {
        return value is { Length: 32 } && value.All(Uri.IsHexDigit);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HerdMe/1.0 (+https://github.com/herdme)");
        return client;
    }
}
