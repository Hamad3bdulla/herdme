using System.Text;
using System.Text.Json;

namespace HerdMe.Windows.Services;

public enum UpdateRollbackKind
{
    // The replaced runtime folder is kept and swapped back.
    Directory,
    // The older version stays installed next to the new one (Node.js); rolling back makes it
    // active again and removes the new one.
    SideBySide
}

public sealed record UpdateRollbackPoint(
    string Id,
    string Name,
    string Version,
    string ReplacedBy,
    string Target,
    DateTimeOffset KeptAt,
    UpdateRollbackKind Kind
);

/// <summary>
/// Keeps the version an update replaced for seven days, so the Updates page can roll back and
/// a runtime that does not start after an update can be put back automatically. Kept in
/// %LOCALAPPDATA%\HerdMe\Rollback\&lt;component&gt;\ (one previous version per component).
/// </summary>
public sealed class UpdateRollbackStore
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
    private const string RuntimeFolder = "runtime";
    private const string MetadataFile = "rollback.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object sync = new();

    private readonly string supportRoot;

    public UpdateRollbackStore(string supportRoot)
    {
        this.supportRoot = Path.GetFullPath(supportRoot);
        Root = Path.Combine(this.supportRoot, "Rollback");
    }

    public string Root { get; }

    // Moves the replaced runtime folder into the rollback area (same volume, so it is a
    // rename). Returns false when it could not be kept; the caller then deletes it as before.
    public bool Keep(string id, string name, string version, string replacedBy, string replacedDirectory, string target, DateTimeOffset now)
    {
        if (!Directory.Exists(replacedDirectory)) return false;
        lock (sync)
        {
            var folder = FolderFor(id);
            try
            {
                TryDelete(folder);
                Directory.CreateDirectory(folder);
                Directory.Move(replacedDirectory, Path.Combine(folder, RuntimeFolder));
                WriteMetadata(folder, new UpdateRollbackPoint(id, name, version, replacedBy, target, now, UpdateRollbackKind.Directory));
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"HerdMe could not keep the previous {id}: {error.Message}");
                TryDelete(folder);
                return false;
            }
        }
    }

    public void KeepSideBySide(string id, string name, string version, string replacedBy, DateTimeOffset now)
    {
        lock (sync)
        {
            var folder = FolderFor(id);
            try
            {
                TryDelete(folder);
                Directory.CreateDirectory(folder);
                WriteMetadata(folder, new UpdateRollbackPoint(id, name, version, replacedBy, string.Empty, now, UpdateRollbackKind.SideBySide));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"HerdMe could not remember the previous {id}: {error.Message}");
            }
        }
    }

    public UpdateRollbackPoint? Find(string id, DateTimeOffset now)
    {
        lock (sync)
        {
            var folder = FolderFor(id);
            var point = ReadMetadata(folder);
            if (point is null) return null;
            if (!IsUsable(point, folder, now))
            {
                TryDelete(folder);
                return null;
            }
            return point;
        }
    }

    public IReadOnlyList<UpdateRollbackPoint> All(DateTimeOffset now)
    {
        lock (sync)
        {
            if (!Directory.Exists(Root)) return [];
            var points = new List<UpdateRollbackPoint>();
            foreach (var folder in SafeEnumerate(Root))
            {
                var point = ReadMetadata(folder);
                if (point is not null && IsUsable(point, folder, now)) points.Add(point);
            }
            return points;
        }
    }

    // Deletes what is older than seven days; called after each check.
    public void Prune(DateTimeOffset now)
    {
        lock (sync)
        {
            if (!Directory.Exists(Root)) return;
            foreach (var folder in SafeEnumerate(Root))
            {
                var point = ReadMetadata(folder);
                if (point is null || !IsUsable(point, folder, now)) TryDelete(folder);
            }
        }
    }

    // Swaps the kept runtime back into place. Whatever runs from Target must be stopped first.
    public void RestoreDirectory(UpdateRollbackPoint point)
    {
        if (point.Kind != UpdateRollbackKind.Directory) throw new InvalidOperationException("This rollback has no kept folder.");
        lock (sync)
        {
            var folder = FolderFor(point.Id);
            var kept = Path.Combine(folder, RuntimeFolder);
            if (!Directory.Exists(kept)) throw new DirectoryNotFoundException("The previous version is no longer available.");
            // Only HerdMe's own runtime folders are ever replaced.
            var target = Path.GetFullPath(point.Target);
            if (!target.StartsWith(Path.TrimEndingDirectorySeparator(supportRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The rollback target is outside HerdMe's runtimes.");
            }
            var swap = Path.Combine(folder, ".replaced-" + Guid.NewGuid().ToString("N"));
            var moved = false;
            if (Directory.Exists(target))
            {
                Directory.Move(target, swap);
                moved = true;
            }
            try
            {
                Directory.Move(kept, target);
            }
            catch
            {
                if (moved && !Directory.Exists(target)) Directory.Move(swap, target);
                throw;
            }
            TryDelete(folder);
        }
    }

    public void Forget(string id)
    {
        lock (sync) TryDelete(FolderFor(id));
    }

    internal string FolderFor(string id)
    {
        var safe = new StringBuilder();
        foreach (var character in id.ToLowerInvariant())
        {
            safe.Append(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' ? character : '-');
        }
        return Path.Combine(Root, safe.ToString().Trim('.'));
    }

    private static bool IsUsable(UpdateRollbackPoint point, string folder, DateTimeOffset now) =>
        now - point.KeptAt < KeepFor
        && point.KeptAt <= now + TimeSpan.FromMinutes(5)
        && (point.Kind == UpdateRollbackKind.SideBySide || Directory.Exists(Path.Combine(folder, RuntimeFolder)));

    private static IEnumerable<string> SafeEnumerate(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static UpdateRollbackPoint? ReadMetadata(string folder)
    {
        try
        {
            var path = Path.Combine(folder, MetadataFile);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<UpdateRollbackPoint>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static void WriteMetadata(string folder, UpdateRollbackPoint point)
    {
        File.WriteAllText(Path.Combine(folder, MetadataFile), JsonSerializer.Serialize(point, JsonOptions), new UTF8Encoding(false));
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"HerdMe will remove {folder} later: {error.Message}");
        }
    }
}
