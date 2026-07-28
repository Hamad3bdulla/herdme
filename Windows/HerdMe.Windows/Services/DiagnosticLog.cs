using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace HerdMe.Windows.Services;

internal static class DiagnosticLog
{
    private static readonly ConcurrentDictionary<string, string> LastFailures =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.Ordinal);

    internal static async Task<bool> WriteFailureAsync(
        string area,
        string eventName,
        string message,
        string? exception = null,
        string? supportRoot = null,
        string? deduplicationScope = null,
        IReadOnlyDictionary<string, string?>? context = null
    )
    {
        var root = supportRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HerdMe"
        );
        root = Path.GetFullPath(root);
        var rootKey = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        var deduplicationKey = string.Join(
            '\u001f',
            rootKey,
            area,
            eventName,
            deduplicationScope ?? string.Empty
        );
        var fingerprint = JsonSerializer.Serialize(new { message, exception, context });
        var gate = Gates.GetOrAdd(deduplicationKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (LastFailures.TryGetValue(deduplicationKey, out var previous)
                && previous == fingerprint)
            {
                return true;
            }
            var payload = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                level = "error",
                area,
                @event = eventName,
                message,
                exception,
                context
            });
            await BoundedLog.AppendLineAsync(Path.Combine(root, "Log", "diagnostics.jsonl"), payload);
            LastFailures[deduplicationKey] = fingerprint;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"HerdMe could not write its diagnostic log: {error.Message}");
            return false;
        }
        finally
        {
            gate.Release();
        }
    }
}
