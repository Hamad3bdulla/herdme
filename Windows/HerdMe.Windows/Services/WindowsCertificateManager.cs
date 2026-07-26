using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HerdMe.Windows.Services;

public sealed class WindowsCertificateManager
{
    private const string AuthorityName = "HerdMe Local Development CA";
    internal const string CredentialScope = "Certificates/v1";
    internal const string AuthorityCredentialKind = "authority";
    internal const string ServerCredentialKind = "server";
    private static readonly TimeSpan ServerRenewalWindow = TimeSpan.FromDays(30);
    private readonly WindowsCredentialStore credentialStore;
    private readonly string certificateDirectory;

    public WindowsCertificateManager()
        : this(
            new WindowsCredentialStore(CredentialScope),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HerdMe",
                "Certificates"
            )
        )
    {
    }

    internal WindowsCertificateManager(
        WindowsCredentialStore credentialStore,
        string certificateDirectory
    )
    {
        this.credentialStore = credentialStore;
        this.certificateDirectory = certificateDirectory;
    }

    public X509Certificate2 PrepareServerCertificate(IEnumerable<string> domains)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows certificate trust requires Windows.");
        }
        using var authority = LoadOrCreateAuthority();
        AddAuthorityToTrustStore(authority);

        var normalizedDomains = NormalizeDomains(domains);
        if (TryLoadServerCertificate(authority, normalizedDomains) is { } cached)
        {
            return cached;
        }

        using var key = RSA.Create(2_048);
        var request = new CertificateRequest(
            "CN=HerdMe Local Site",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true
        ));
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var names = new SubjectAlternativeNameBuilder();
        foreach (var domain in normalizedDomains)
        {
            names.AddDnsName(domain);
        }
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        var serial = RandomNumberGenerator.GetBytes(16);
        using var certificate = request.Create(
            authority,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(825),
            serial
        );
        using var certificateWithKey = certificate.CopyWithPrivateKey(key);
        SaveServerCertificate(certificateWithKey, normalizedDomains);
        return LoadCachedServerCertificate();
    }

    public bool IsAuthorityTrusted()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var certificatePath = Path.Combine(certificateDirectory, "authority.pfx");
        if (!File.Exists(certificatePath)) return false;

        using var authority = TryLoadProtectedPkcs12(certificatePath, AuthorityCredentialKind);
        if (authority is null) return false;
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(
            X509FindType.FindByThumbprint,
            authority.Thumbprint,
            validOnly: false
        ).Count > 0;
    }

    public void TrustAuthority()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows certificate trust requires Windows.");
        }
        using var authority = LoadOrCreateAuthority();
        AddAuthorityToTrustStore(authority);
    }

    private X509Certificate2 LoadOrCreateAuthority()
    {
        var certificatePath = Path.Combine(certificateDirectory, "authority.pfx");
        if (File.Exists(certificatePath)
            && TryLoadProtectedPkcs12(certificatePath, AuthorityCredentialKind) is { } existing)
        {
            return existing;
        }

        Directory.CreateDirectory(certificateDirectory);
        using var key = RSA.Create(3_072);
        var request = new CertificateRequest(
            $"CN={AuthorityName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true
        ));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10)
        );
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        SaveProtectedPkcs12(
            certificatePath,
            AuthorityCredentialKind,
            generated.Export(X509ContentType.Pfx, password),
            password
        );
        return LoadProtectedPkcs12(certificatePath, AuthorityCredentialKind);
    }

    private static void AddAuthorityToTrustStore(X509Certificate2 authority)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            authority.Thumbprint,
            validOnly: false
        );
        if (existing.Count == 0)
        {
            using var publicCertificate = new X509Certificate2(authority.Export(X509ContentType.Cert));
            store.Add(publicCertificate);
        }
    }

    internal static IReadOnlyList<string> NormalizeDomains(IEnumerable<string> domains)
    {
        return domains
            .Select(domain => domain.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(domain => Uri.CheckHostName(domain) == UriHostNameType.Dns)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string ServerCertificateCacheKey(IEnumerable<string> domains)
    {
        return "v1\n" + string.Join("\n", NormalizeDomains(domains));
    }

    private X509Certificate2? TryLoadServerCertificate(
        X509Certificate2 authority,
        IReadOnlyList<string> domains
    )
    {
        var keyPath = Path.Combine(certificateDirectory, "server-domains.txt");
        try
        {
            if (!File.Exists(keyPath)
                || File.ReadAllText(keyPath) != ServerCertificateCacheKey(domains))
            {
                return null;
            }
            var certificate = LoadCachedServerCertificate();
            if (!certificate.HasPrivateKey
                || certificate.NotAfter.ToUniversalTime()
                    <= DateTime.UtcNow.Add(ServerRenewalWindow)
                || !certificate.Issuer.Equals(authority.Subject, StringComparison.Ordinal))
            {
                certificate.Dispose();
                return null;
            }
            return certificate;
        }
        catch (Exception error) when (error is IOException or CryptographicException)
        {
            return null;
        }
    }

    private void SaveServerCertificate(
        X509Certificate2 certificate,
        IReadOnlyList<string> domains
    )
    {
        Directory.CreateDirectory(certificateDirectory);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        SaveProtectedPkcs12(
            Path.Combine(certificateDirectory, "server.pfx"),
            ServerCredentialKind,
            certificate.Export(X509ContentType.Pfx, password),
            password
        );
        WriteAtomically(
            Path.Combine(certificateDirectory, "server-domains.txt"),
            System.Text.Encoding.UTF8.GetBytes(ServerCertificateCacheKey(domains))
        );
    }

    private X509Certificate2 LoadCachedServerCertificate()
    {
        return LoadProtectedPkcs12(
            Path.Combine(certificateDirectory, "server.pfx"),
            ServerCredentialKind
        );
    }

    private static void WriteAtomically(string path, byte[] contents)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, contents);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private X509Certificate2? TryLoadProtectedPkcs12(string path, string kind)
    {
        var contents = File.ReadAllBytes(path);
        var account = PasswordAccount(kind, contents);
        var legacyPath = Path.Combine(certificateDirectory, kind + ".password");
        var password = credentialStore.ReadOrMigrate(
            account,
            legacyPath,
            validator: candidate => CanLoadPkcs12(contents, candidate)
        );
        return password is null ? null : LoadPkcs12(contents, password);
    }

    private X509Certificate2 LoadProtectedPkcs12(string path, string kind)
    {
        return TryLoadProtectedPkcs12(path, kind)
            ?? throw new CryptographicException("The certificate credential is missing.");
    }

    private void SaveProtectedPkcs12(
        string path,
        string kind,
        byte[] contents,
        string password
    )
    {
        var account = PasswordAccount(kind, contents);
        string? previousAccount = null;
        if (File.Exists(path))
        {
            previousAccount = PasswordAccount(kind, File.ReadAllBytes(path));
        }

        credentialStore.Write(account, password);
        try
        {
            WriteAtomically(path, contents);
        }
        catch
        {
            if (!string.Equals(account, previousAccount, StringComparison.Ordinal))
            {
                credentialStore.Delete(account);
            }
            throw;
        }

        var legacyPath = Path.Combine(certificateDirectory, kind + ".password");
        if (File.Exists(legacyPath)) File.Delete(legacyPath);
        if (previousAccount is not null
            && !string.Equals(previousAccount, account, StringComparison.Ordinal))
        {
            credentialStore.Delete(previousAccount);
        }
    }

    internal static string PasswordAccount(string kind, ReadOnlySpan<byte> pkcs12)
    {
        if (kind is not AuthorityCredentialKind and not ServerCredentialKind)
        {
            throw new ArgumentException("Unsupported certificate credential kind.", nameof(kind));
        }
        var digest = Convert.ToHexString(SHA256.HashData(pkcs12)).ToLowerInvariant();
        return kind + "-pfx-password-" + digest;
    }

    private static bool CanLoadPkcs12(byte[] contents, string password)
    {
        try
        {
            using var certificate = LoadPkcs12(contents, password);
            return certificate.HasPrivateKey;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

#pragma warning disable SYSLIB0057
    private static X509Certificate2 LoadPkcs12(byte[] contents, string password)
    {
        return new X509Certificate2(
            contents,
            password,
            X509KeyStorageFlags.EphemeralKeySet
        );
    }
#pragma warning restore SYSLIB0057
}
