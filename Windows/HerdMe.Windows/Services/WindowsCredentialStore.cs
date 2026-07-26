using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HerdMe.Windows.Services;

internal interface IWindowsCredentialBackend
{
    bool TryRead(string target, out string secret);
    void Write(string target, string username, string secret);
    void Delete(string target);
}

public sealed class WindowsCredentialStore
{
    private const string TargetRoot = "HerdMe/";
    private readonly IWindowsCredentialBackend backend;
    private readonly string scope;
    private readonly object sync = new();

    public WindowsCredentialStore(string scope)
        : this(scope, new WindowsCredentialManagerBackend())
    {
    }

    internal WindowsCredentialStore(string scope, IWindowsCredentialBackend backend)
    {
        _ = BuildTarget(scope, "validation");
        this.scope = scope;
        this.backend = backend;
    }

    public string? Read(string account)
    {
        lock (sync)
        {
            if (!backend.TryRead(BuildTarget(scope, account), out var secret)) return null;
            ValidateStoredSecret(secret);
            return secret;
        }
    }

    public void Write(string account, string secret, string username = "HerdMe")
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("Credential values cannot be empty.", nameof(secret));
        }
        if (Encoding.UTF8.GetByteCount(secret) > WindowsCredentialManagerBackend.MaximumBlobBytes)
        {
            throw new ArgumentException("Credential values cannot exceed 2,560 UTF-8 bytes.", nameof(secret));
        }

        lock (sync)
        {
            backend.Write(BuildTarget(scope, account), username, secret);
        }
    }

    public string? ReadOrMigrate(
        string account,
        string legacyPath,
        string username = "HerdMe",
        Func<string, bool>? validator = null
    )
    {
        lock (sync)
        {
            var target = BuildTarget(scope, account);
            if (backend.TryRead(target, out var stored))
            {
                ValidateStoredSecret(stored);
                if (validator is not null && !validator(stored))
                {
                    throw new InvalidDataException("The protected credential cannot unlock its data.");
                }
                DeleteLegacyFile(legacyPath);
                return stored;
            }
            if (!File.Exists(legacyPath)) return null;

            var legacy = File.ReadAllText(legacyPath).Trim();
            if (string.IsNullOrEmpty(legacy))
            {
                throw new InvalidDataException("The legacy credential file is empty.");
            }
            if (Encoding.UTF8.GetByteCount(legacy) > WindowsCredentialManagerBackend.MaximumBlobBytes)
            {
                throw new InvalidDataException("The legacy credential is too large for Credential Manager.");
            }

            backend.Write(target, username, legacy);
            if (!backend.TryRead(target, out var verified) || !SecretsMatch(legacy, verified))
            {
                throw new IOException("HerdMe could not verify the migrated credential.");
            }
            ValidateStoredSecret(verified);
            if (validator is not null && !validator(verified))
            {
                backend.Delete(target);
                throw new InvalidDataException("The migrated credential cannot unlock its data.");
            }

            DeleteLegacyFile(legacyPath);
            return verified;
        }
    }

    public void Delete(string account)
    {
        lock (sync)
        {
            backend.Delete(BuildTarget(scope, account));
        }
    }

    internal static string BuildTarget(string scope, string account)
    {
        if (!IsValidPath(scope, allowSeparators: true))
        {
            throw new ArgumentException("Credential scopes contain unsupported characters.", nameof(scope));
        }
        if (!IsValidPath(account, allowSeparators: false))
        {
            throw new ArgumentException("Credential accounts contain unsupported characters.", nameof(account));
        }
        return TargetRoot + scope + "/" + account;
    }

    private static bool IsValidPath(string value, bool allowSeparators)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 192) return false;
        var segments = allowSeparators ? value.Split('/') : [value];
        return segments.All(segment => segment.Length is > 0 and <= 128
            && segment.All(character => character is '-'
                or '_'
                or '.'
                or >= '0' and <= '9'
                or >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'));
    }

    private static void DeleteLegacyFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void ValidateStoredSecret(string secret)
    {
        var byteCount = Encoding.UTF8.GetByteCount(secret);
        if (byteCount is <= 0 or > WindowsCredentialManagerBackend.MaximumBlobBytes)
        {
            throw new InvalidDataException("The protected credential is invalid.");
        }
    }

    private static bool SecretsMatch(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        try
        {
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }
}

internal sealed class WindowsCredentialManagerBackend : IWindowsCredentialBackend
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal const int MaximumBlobBytes = 2_560;
    private const int GenericCredential = 1;
    private const int PersistLocalMachine = 2;
    private const int ErrorNotFound = 1_168;

    public bool TryRead(string target, out string secret)
    {
        EnsureWindows();
        secret = string.Empty;
        if (!CredRead(target, GenericCredential, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return false;
            throw new Win32Exception(error, "HerdMe could not read its protected credentials.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize <= 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                throw new InvalidDataException("The saved credential is empty.");
            }
            if (credential.CredentialBlobSize > MaximumBlobBytes)
            {
                throw new InvalidDataException("The saved credential is too large.");
            }
            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                secret = StrictUtf8.GetString(bytes);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Write(string target, string username, string secret)
    {
        EnsureWindows();
        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = username
            };
            if (!CredWrite(ref credential, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "HerdMe could not save its protected credentials.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string target)
    {
        EnsureWindows();
        if (CredDelete(target, GenericCredential, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error, "HerdMe could not remove its protected credentials.");
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager requires Windows.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
