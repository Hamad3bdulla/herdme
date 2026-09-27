namespace HerdMe.Windows.Services;

public enum ErrorKind
{
    General,
    AccessDenied,
    FileInUse,
    NotFound,
    PortInUse,
    Network,
    Timeout,
    Cancelled
}

/// <summary>
/// Turns a raw failure into a short human title for the error dialog. The raw message stays
/// in the details (and is what "Copy details" copies), so nothing is hidden, only headed.
/// </summary>
public static class ErrorPresentation
{
    private const int AccessDeniedHResult = unchecked((int)0x80070005);
    private const int SharingViolationHResult = unchecked((int)0x80070020);
    private const int LockViolationHResult = unchecked((int)0x80070021);
    private const int AddressInUse = 10048;

    public static ErrorKind Classify(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            OperationCanceledException => ErrorKind.Cancelled,
            TimeoutException => ErrorKind.Timeout,
            UnauthorizedAccessException => ErrorKind.AccessDenied,
            FileNotFoundException or DirectoryNotFoundException => ErrorKind.NotFound,
            System.Net.Sockets.SocketException { ErrorCode: AddressInUse } => ErrorKind.PortInUse,
            System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException => ErrorKind.Network,
            IOException { HResult: SharingViolationHResult or LockViolationHResult } => ErrorKind.FileInUse,
            IOException { HResult: AccessDeniedHResult } => ErrorKind.AccessDenied,
            _ => Classify(error.Message)
        };
    }

    // Pages mostly hand over the message text; the OS wording is English on most machines,
    // and anything unrecognised just gets the general title.
    public static ErrorKind Classify(string? message)
    {
        var text = message ?? string.Empty;
        if (Contains(text, "access is denied") || Contains(text, "access to the path")
            || Contains(text, "permission")) return ErrorKind.AccessDenied;
        if (Contains(text, "being used by another process") || Contains(text, "locked a portion"))
        {
            return ErrorKind.FileInUse;
        }
        if (Contains(text, "only one usage of each socket address") || Contains(text, "address already in use")
            || Contains(text, "port") && Contains(text, "in use")) return ErrorKind.PortInUse;
        if (Contains(text, "timed out") || Contains(text, "timeout")) return ErrorKind.Timeout;
        if (Contains(text, "could not find") || Contains(text, "cannot find") || Contains(text, "does not exist")
            || Contains(text, "not found")) return ErrorKind.NotFound;
        if (Contains(text, "no such host") || Contains(text, "connection refused")
            || Contains(text, "network")) return ErrorKind.Network;
        return ErrorKind.General;
    }

    public static string TitleKey(ErrorKind kind) => kind switch
    {
        ErrorKind.AccessDenied => "ErrorTitleAccessDenied",
        ErrorKind.FileInUse => "ErrorTitleFileInUse",
        ErrorKind.NotFound => "ErrorTitleNotFound",
        ErrorKind.PortInUse => "ErrorTitlePortInUse",
        ErrorKind.Network => "ErrorTitleNetwork",
        ErrorKind.Timeout => "ErrorTitleTimeout",
        ErrorKind.Cancelled => "ErrorTitleCancelled",
        _ => "ErrorTitleGeneral"
    };

    public static string HintKey(ErrorKind kind) => kind switch
    {
        ErrorKind.AccessDenied => "ErrorHintAccessDenied",
        ErrorKind.FileInUse => "ErrorHintFileInUse",
        ErrorKind.NotFound => "ErrorHintNotFound",
        ErrorKind.PortInUse => "ErrorHintPortInUse",
        ErrorKind.Network => "ErrorHintNetwork",
        ErrorKind.Timeout => "ErrorHintTimeout",
        ErrorKind.Cancelled => "ErrorHintCancelled",
        _ => "ErrorHintGeneral"
    };

    // What "Copy details" puts on the clipboard: enough for an issue report, no secrets added.
    public static string Details(string title, string message, string? details, string version, DateTimeOffset time)
    {
        var lines = new List<string>
        {
            title,
            message.Trim(),
            string.Empty,
            $"HerdMe {version}",
            $"Windows {Environment.OSVersion.Version}",
            time.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(details) && !details.Trim().Equals(message.Trim(), StringComparison.Ordinal))
        {
            lines.Add(string.Empty);
            lines.Add(details.Trim());
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string DiagnosticsLogPath(string supportRoot) =>
        Path.Combine(supportRoot, "Log", "diagnostics.jsonl");

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
