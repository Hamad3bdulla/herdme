using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace HerdMe.Windows.Services;

internal static class DiagnosticLog
{
    private static readonly ConcurrentDictionary<string, string> LastFailures =
        new(StringComparer.Ordinal);

    internal static async Task<bool> WriteFailureAsync(
        string area,
        string eventName,
        string message,
        string? exception = null,
        string? supportRoot = null
    )
    {
        var deduplicationKey = area + "|" + eventName;
        var fingerprint = message + "|" + exception;
        if (LastFailures.TryGetValue(deduplicationKey, out var previous) && previous == fingerprint)
        {
            return true;
        }

        var root = supportRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
        var payload = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            level = "error",
            area,
            @event = eventName,
            message,
            exception
        });
        try
        {
            await BoundedLog.AppendLineAsync(Path.Combine(root, "Log", "diagnostics.jsonl"), payload);
            LastFailures[deduplicationKey] = fingerprint;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"HerdMe could not write its diagnostic log: {error.Message}");
            return false;
        }
    }
}
