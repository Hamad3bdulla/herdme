using System.Security.Cryptography;
using System.Text;

namespace HerdMe.Windows.Services;

public sealed record ProjectEnvironmentRevision(bool Exists, string? Digest)
{
    public static ProjectEnvironmentRevision Missing { get; } = new(false, null);
}

public sealed record ProjectEnvironmentDocument(
    string Contents,
    bool Exists,
    bool LoadedFromExample,
    ProjectEnvironmentRevision Revision
);

public sealed class ProjectEnvironmentChangedException : IOException
{
    public ProjectEnvironmentChangedException()
        : base("The .env file changed outside HerdMe. Reload it before saving so those changes are not overwritten.")
    {
    }
}

public static class ProjectEnvironmentFile
{
    public const int MaximumFileBytes = 4 * 1_024 * 1_024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static ProjectEnvironmentDocument Load(string projectPath)
    {
        var fullProjectPath = RequireProjectDirectory(projectPath);
        var environmentPath = Path.Combine(fullProjectPath, ".env");
        var examplePath = Path.Combine(fullProjectPath, ".env.example");

        if (File.Exists(environmentPath))
        {
            var data = ReadData(environmentPath);
            return new ProjectEnvironmentDocument(
                Decode(data),
                true,
                false,
                Revision(data)
            );
        }

        if (File.Exists(examplePath))
        {
            var data = ReadData(examplePath);
            return new ProjectEnvironmentDocument(
                Decode(data),
                false,
                true,
                ProjectEnvironmentRevision.Missing
            );
        }

        return new ProjectEnvironmentDocument(
            string.Empty,
            false,
            false,
            ProjectEnvironmentRevision.Missing
        );
    }

    public static ProjectEnvironmentDocument Save(
        string projectPath,
        string contents,
        ProjectEnvironmentRevision expectedRevision
    )
    {
        var fullProjectPath = RequireProjectDirectory(projectPath);
        var environmentPath = Path.Combine(fullProjectPath, ".env");
        var data = Utf8.GetBytes(contents);
        if (data.Length > MaximumFileBytes)
        {
            throw new InvalidDataException(
                "The project's .env file is larger than the supported 4 MB limit."
            );
        }

        var currentRevision = File.Exists(environmentPath)
            ? Revision(ReadData(environmentPath))
            : ProjectEnvironmentRevision.Missing;
        if (currentRevision != expectedRevision) throw new ProjectEnvironmentChangedException();

        RejectReparsePoint(environmentPath);
        var temporaryPath = Path.Combine(
            fullProjectPath,
            $".env.herdme-{Guid.NewGuid():N}.tmp"
        );
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1_024,
                FileOptions.WriteThrough
            ))
            {
                stream.Write(data);
                stream.Flush(true);
            }
            RejectReparsePoint(environmentPath);
            File.Move(temporaryPath, environmentPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return new ProjectEnvironmentDocument(
            contents,
            true,
            false,
            Revision(data)
        );
    }

    private static string RequireProjectDirectory(string projectPath)
    {
        var fullProjectPath = Path.GetFullPath(projectPath);
        if (!Directory.Exists(fullProjectPath))
        {
            throw new DirectoryNotFoundException(
                "The selected project directory is no longer available."
            );
        }
        return fullProjectPath;
    }

    private static byte[] ReadData(string path)
    {
        RejectReparsePoint(path);
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException(
                "The project's .env file must be a regular UTF-8 text file."
            );
        }
        if (info.Length > MaximumFileBytes)
        {
            throw new InvalidDataException(
                "The project's .env file is larger than the supported 4 MB limit."
            );
        }
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
    }

    private static string Decode(byte[] data)
    {
        try
        {
            return Utf8.GetString(data).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException(
                "The project's .env file must be a regular UTF-8 text file.",
                error
            );
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "HerdMe will not modify a symbolic .env file. Replace it with a project-owned file first."
            );
        }
        if (Directory.Exists(path))
        {
            throw new InvalidDataException(
                "The project's .env file must be a regular UTF-8 text file."
            );
        }
    }

    private static ProjectEnvironmentRevision Revision(byte[] data)
    {
        return new ProjectEnvironmentRevision(
            true,
            Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()
        );
    }
}
