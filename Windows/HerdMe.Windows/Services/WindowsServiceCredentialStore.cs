using System.Security.Cryptography;

namespace HerdMe.Windows.Services;

public sealed record ServiceCredentials(string Username, string Secret)
{
    public bool IsValid => Username.Length >= 3
        && Secret.Length >= 32
        && Username.All(IsEnvironmentSafe)
        && Secret.All(IsEnvironmentSafe);

    private static bool IsEnvironmentSafe(char character) => character is '-'
        or '_'
        or >= '0' and <= '9'
        or >= 'A' and <= 'Z'
        or >= 'a' and <= 'z';
}

public static class ServiceCredentialGenerator
{
    public static ServiceCredentials Create(Guid identifier)
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var secret = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var compactIdentifier = identifier.ToString("N");
        return new ServiceCredentials($"herdme_{compactIdentifier[..12]}", secret);
    }
}

public sealed class WindowsServiceCredentialStore
{
    private const string CredentialScope = "ManagedServices/v1";
    private readonly WindowsCredentialStore store;
    private readonly object sync = new();

    public WindowsServiceCredentialStore()
        : this(new WindowsCredentialStore(CredentialScope))
    {
    }

    internal WindowsServiceCredentialStore(WindowsCredentialStore store)
    {
        this.store = store;
    }

    public ServiceCredentials GetOrCreate(Guid identifier)
    {
        lock (sync)
        {
            var username = Username(identifier);
            if (store.Read(Account(identifier)) is { } secret)
            {
                var existing = new ServiceCredentials(username, secret);
                if (!existing.IsValid)
                {
                    throw new InvalidDataException(
                        "This service's saved credentials are invalid. Delete and add the service again."
                    );
                }
                return existing;
            }

            var credentials = ServiceCredentialGenerator.Create(identifier);
            store.Write(Account(identifier), credentials.Secret, credentials.Username);
            return credentials;
        }
    }

    public void Delete(Guid identifier)
    {
        lock (sync)
        {
            if (OperatingSystem.IsWindows()) store.Delete(Account(identifier));
        }
    }

    private static string Account(Guid identifier) => identifier.ToString("D");

    private static string Username(Guid identifier) => "herdme_" + identifier.ToString("N")[..12];
}
