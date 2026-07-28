namespace HerdMe.Windows.Models;

public sealed record ProductLink(string Title, Uri Uri);

public static class ProductLinks
{
    public static ProductLink Repository { get; } = new(
        "Repository",
        new Uri("https://github.com/Hamad3bdulla/herdme")
    );

    public static ProductLink Documentation { get; } = new(
        "Documentation",
        new Uri("https://github.com/Hamad3bdulla/herdme/tree/master/docs")
    );

    public static ProductLink ReleaseNotes { get; } = new(
        "Release Notes",
        new Uri("https://github.com/Hamad3bdulla/herdme/releases")
    );

    public static IReadOnlyList<ProductLink> All { get; } = Array.AsReadOnly(
        new[] { Repository, Documentation, ReleaseNotes }
    );
}
