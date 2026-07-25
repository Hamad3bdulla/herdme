namespace HerdMe.Windows.Services;

public sealed class SiteRuntimeStore
{
    public void SetPhp(string sitePath, string? cycle) => Set(sitePath, ".herdme-php", cycle, IsPhpCycle);

    public void SetNode(string sitePath, string? version) => Set(sitePath, ".herdme-node", version, IsNodeVersion);

    private static void Set(
        string sitePath,
        string fileName,
        string? value,
        Func<string, bool> validator
    )
    {
        var root = Path.GetFullPath(sitePath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var destination = Path.Combine(root, fileName);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (File.Exists(destination)) File.Delete(destination);
            return;
        }
        var normalized = value.Trim();
        if (!validator(normalized)) throw new ArgumentException("The selected runtime version is invalid.");
        var temporary = destination + ".tmp";
        File.WriteAllText(temporary, normalized + Environment.NewLine);
        File.Move(temporary, destination, true);
    }

    private static bool IsPhpCycle(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 2 && parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit));
    }

    private static bool IsNodeVersion(string value)
    {
        return Version.TryParse(value.TrimStart('v'), out _);
    }
}
