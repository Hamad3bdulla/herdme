using System.Net.Sockets;
using System.Diagnostics;
using System.Text.Json;

namespace HerdMe.Windows.Services;

public sealed record RuntimeHealthResult(string Name, bool Healthy, string Detail);

public static class RuntimeHealthInspector
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(3)
    }) { Timeout = TimeSpan.FromSeconds(6) };

    public static async Task<RuntimeHealthResult> InspectSiteAsync(
        string domain,
        bool https,
        CancellationToken cancellationToken = default
    )
    {
        var scheme = https ? "https" : "http";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{scheme}://{domain}/");
            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            var code = (int)response.StatusCode;
            return new RuntimeHealthResult(
                "HTTP response",
                code is >= 200 and < 400,
                $"{scheme.ToUpperInvariant()} {code} {response.ReasonPhrase}".Trim()
            );
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return new RuntimeHealthResult("HTTP response", false, error.Message);
        }
    }

    public static async Task<RuntimeHealthResult> InspectTcpServiceAsync(
        string name,
        int port,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            return new RuntimeHealthResult(name, true, $"Port {port} accepts connections");
        }
        catch (Exception error) when (error is SocketException or TimeoutException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new RuntimeHealthResult(name, false, $"Port {port}: {error.Message}");
        }
    }

    public static IReadOnlyList<string> ComposerPlatformRequirements(string composerJsonPath)
    {
        if (!File.Exists(composerJsonPath)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(composerJsonPath));
            if (!document.RootElement.TryGetProperty("require", out var require)
                || require.ValueKind != JsonValueKind.Object) return [];
            return require.EnumerateObject()
                .Where(property => property.Name.Equals("php", StringComparison.OrdinalIgnoreCase)
                    || property.Name.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
                .Select(property => $"{property.Name} {property.Value.GetString()}".Trim())
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return ["composer.json is invalid"];
        }
    }

    public static string NodePackageManager(string projectPath)
    {
        if (File.Exists(Path.Combine(projectPath, "pnpm-lock.yaml"))) return "pnpm";
        if (File.Exists(Path.Combine(projectPath, "yarn.lock"))) return "yarn";
        return "npm";
    }

    public static async Task<bool> GitTracksEnvironmentAsync(
        string gitExecutable,
        string projectPath,
        CancellationToken cancellationToken = default
    )
    {
        var startInfo = new ProcessStartInfo(gitExecutable)
        {
            WorkingDirectory = projectPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("ls-files");
        startInfo.ArgumentList.Add("--error-unmatch");
        startInfo.ArgumentList.Add(".env");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git could not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(output, error);
        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(await output);
    }
}
