namespace HerdMe.Windows.Models;

public sealed class LogFileRecord
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public long Size { get; init; }

    public DateTimeOffset ModifiedAt { get; init; }

    public string SizeText => Size < 1_024
        ? $"{Size} B"
        : Size < 1_024 * 1_024 ? $"{Size / 1_024.0:F1} KB" : $"{Size / 1_024.0 / 1_024.0:F1} MB";
}
