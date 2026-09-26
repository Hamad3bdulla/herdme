using System.Text;
using System.Text.Json;
using HerdMe.Windows.Models;

namespace HerdMe.Windows.Services;

internal static class CaptureExport
{
    internal static Task SaveMailAsync(CapturedMail message, string destination, CancellationToken cancellationToken = default) =>
        WriteAsync(destination, async (stream, token) =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(message.Raw.AsMemory(), token);
            await writer.FlushAsync(token);
        }, cancellationToken);

    internal static Task SaveDumpAsync(CapturedDump dump, string destination, CancellationToken cancellationToken = default) =>
        WriteAsync(destination, (stream, token) => JsonSerializer.SerializeAsync(stream, dump,
            cancellationToken: token), cancellationToken);

    private static async Task WriteAsync(
        string destination,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = Path.GetFullPath(destination);
        var temporary = Path.Combine(Path.GetDirectoryName(output)!, $".herdme-capture-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1_024, FileOptions.Asynchronous))
            {
                await write(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
