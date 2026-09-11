namespace HerdMe.Windows.Services;

public static class InstallationPreflight
{
    public static void EnsureStorage(string destination, long requiredBytes = 512L * 1024 * 1024)
    {
        var fullPath = Path.GetFullPath(destination);
        var drive = new DriveInfo(Path.GetPathRoot(fullPath)!);
        if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
            throw new IOException($"Insufficient disk space. Required: {requiredBytes / 1048576} MB.");
        Directory.CreateDirectory(fullPath);
        var probe = Path.Combine(fullPath, $".write-check-{Guid.NewGuid():N}");
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1, FileOptions.DeleteOnClose);
        stream.WriteByte(0);
    }
}
