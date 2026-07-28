namespace HerdMe.Windows.Models;

public sealed class LogSourceRecord
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string RootPath { get; set; } = string.Empty;

    public string FallbackPath { get; set; } = string.Empty;

    public bool IsApplication { get; set; }
}

public sealed class LogFileRecord
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTimeOffset ModifiedAt { get; set; }

    public string SizeText => Size < 1_024
        ? $"{Size} B"
        : Size < 1_024 * 1_024 ? $"{Size / 1_024.0:F1} KB" : $"{Size / 1_024.0 / 1_024.0:F1} MB";
}
