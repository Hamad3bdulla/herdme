using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

public static class TablePlusConnection
{
    public static bool Supports(string definitionId) => TargetFor(definitionId) is not null;

    public static string? DisplayAddress(ManagedServiceInstance instance)
    {
        if (instance.Port is <= 0 or > 65_535 || TargetFor(instance.DefinitionId) is not { } target)
        {
            return null;
        }
        return new UriBuilder
        {
            Scheme = target.Scheme,
            Host = "127.0.0.1",
            Port = instance.Port,
            Path = target.Database
        }.Uri.AbsoluteUri;
    }

    public static Uri? UriFor(
        ManagedServiceInstance instance,
        ServiceCredentials? credentials = null
    )
    {
        if (instance.Port is <= 0 or > 65_535) return null;
        if (TargetFor(instance.DefinitionId) is not { } target
            || target.RequiresCredentials && credentials is null) return null;

        var builder = new UriBuilder
        {
            Scheme = target.Scheme,
            Host = "127.0.0.1",
            Port = instance.Port,
            Path = target.Database,
            UserName = target.RequiresCredentials ? credentials?.Username : string.Empty,
            Password = target.RequiresCredentials ? credentials?.Secret : null
        };
        return builder.Uri;
    }

    public static void Open(
        ManagedServiceInstance instance,
        ServiceCredentials? credentials = null
    )
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("TablePlus launching requires Windows.");
        }
        var uri = UriFor(instance, credentials)
            ?? throw new NotSupportedException(
                $"TablePlus connections are not available for {instance.Name}."
            );
        Open(uri);
    }

    public static void Open(Uri uri)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("TablePlus launching requires Windows.");
        }
        var executable = FindExecutable()
            ?? throw new FileNotFoundException(
                "Install TablePlus before opening this database service."
            );
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(uri.AbsoluteUri);
        try
        {
            Process.Start(startInfo);
        }
        catch (Win32Exception error)
        {
            throw new InvalidOperationException("Windows could not open TablePlus.", error);
        }
    }

    private static (string Scheme, string Database, bool RequiresCredentials)? TargetFor(
        string definitionId
    ) => definitionId.ToLowerInvariant() switch
    {
        "mysql" => ("mysql", "mysql", true),
        "mariadb" => ("mariadb", "mysql", true),
        "postgresql" => ("postgresql", "postgres", true),
        "mongodb" => ("mongodb", "admin", false),
        "redis" or "valkey" => ("redis", "0", false),
        _ => null
    };

    private static string? FindExecutable()
    {
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\App Paths\TablePlus.exe"
            );
            if (key?.GetValue(null) is string path && File.Exists(path)) return path;
        }

        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "TablePlus", "TablePlus.exe"
            ),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "TablePlus", "TablePlus.exe"
            ),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "TablePlus", "TablePlus.exe"
            )
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
