using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HerdMe.Windows.Services;

public sealed class WindowsCertificateManager
{
    private const string AuthorityName = "HerdMe Local Development CA";

    public X509Certificate2 PrepareServerCertificate(IEnumerable<string> domains)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows certificate trust requires Windows.");
        }
        using var authority = LoadOrCreateAuthority();
        AddAuthorityToTrustStore(authority);

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
        foreach (var domain in domains
            .Select(domain => domain.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            names.AddDnsName(domain);
        }
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        var serial = RandomNumberGenerator.GetBytes(16);
        var certificate = request.Create(
            authority,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(825),
            serial
        );
        return certificate.CopyWithPrivateKey(key);
    }

    public bool IsAuthorityTrusted()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var directory = CertificateDirectory();
        var certificatePath = Path.Combine(directory, "authority.pfx");
        var passwordPath = Path.Combine(directory, "authority.password");
        if (!File.Exists(certificatePath) || !File.Exists(passwordPath)) return false;

        using var authority = LoadPkcs12(certificatePath, File.ReadAllText(passwordPath).Trim());
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

    private static X509Certificate2 LoadOrCreateAuthority()
    {
        var directory = CertificateDirectory();
        var certificatePath = Path.Combine(directory, "authority.pfx");
        var passwordPath = Path.Combine(directory, "authority.password");
        if (File.Exists(certificatePath) && File.Exists(passwordPath))
        {
            return LoadPkcs12(certificatePath, File.ReadAllText(passwordPath).Trim());
        }

        Directory.CreateDirectory(directory);
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
        File.WriteAllBytes(certificatePath, generated.Export(X509ContentType.Pfx, password));
        File.WriteAllText(passwordPath, password);
        return LoadPkcs12(certificatePath, password);
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

    private static string CertificateDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe",
            "Certificates"
        );
    }

#pragma warning disable SYSLIB0057
    private static X509Certificate2 LoadPkcs12(string path, string password)
    {
        return new X509Certificate2(
            path,
            password,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable
        );
    }
#pragma warning restore SYSLIB0057
}
