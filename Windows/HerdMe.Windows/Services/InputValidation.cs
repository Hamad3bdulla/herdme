namespace HerdMe.Windows.Services;

public enum InputSeverity
{
    Valid,
    Warning,
    Error
}

// MessageKey is a resw key; Argument (when set) fills its {0}.
public sealed record InputCheck(InputSeverity Severity, string MessageKey, string? Argument = null)
{
    public static InputCheck Ok { get; } = new(InputSeverity.Valid, string.Empty);

    public bool Blocks => Severity == InputSeverity.Error;
}

/// <summary>
/// Checks a field while the user types, so problems show under the field before Save or
/// Add is pressed. The checks only read state; the caller passes in what "taken" means.
/// </summary>
public static class InputValidation
{
    public const int MaximumServiceNameLength = 64;

    // Sites are https://name.test/ on 80 and 443; a service there would break every site.
    public static readonly IReadOnlySet<int> SitePorts = new HashSet<int> { 80, 443 };

    public static InputCheck Port(double value, IEnumerable<int> herdMePorts, Func<int, bool> isFree)
    {
        ArgumentNullException.ThrowIfNull(herdMePorts);
        ArgumentNullException.ThrowIfNull(isFree);
        if (double.IsNaN(value)) return new(InputSeverity.Error, "InputPortRequired");
        if (value != Math.Floor(value) || value < 1 || value > 65_535)
        {
            return new(InputSeverity.Error, "InputPortRange");
        }
        var port = (int)value;
        if (SitePorts.Contains(port)) return new(InputSeverity.Error, "InputPortSites", port.ToString());
        if (herdMePorts.Contains(port)) return new(InputSeverity.Error, "InputPortHerdMe", port.ToString());
        if (!isFree(port)) return new(InputSeverity.Error, "InputPortBusy", port.ToString());
        return port < 1024
            ? new(InputSeverity.Warning, "InputPortLow", port.ToString())
            : new(InputSeverity.Valid, "InputPortFree", port.ToString());
    }

    public static InputCheck ServiceName(string? text, IEnumerable<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(existingNames);
        var name = (text ?? string.Empty).Trim();
        // Empty is fine: the service keeps its default name.
        if (name.Length == 0) return InputCheck.Ok;
        if (name.Length > MaximumServiceNameLength)
        {
            return new(InputSeverity.Error, "InputNameTooLong", MaximumServiceNameLength.ToString());
        }
        if (name.Any(char.IsControl)) return new(InputSeverity.Error, "InputNameInvalid");
        return existingNames.Any(existing => existing.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            ? new(InputSeverity.Warning, "InputNameDuplicate", name)
            : InputCheck.Ok;
    }

    public static InputCheck FolderPath(string? text, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        var value = (text ?? string.Empty).Trim().Trim('"');
        if (value.Length == 0) return InputCheck.Ok;
        if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || value.IndexOfAny(['*', '?', '<', '>', '|']) >= 0)
        {
            return new(InputSeverity.Error, "InputPathInvalid");
        }
        if (!Path.IsPathFullyQualified(value)) return new(InputSeverity.Error, "InputPathNotAbsolute");
        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(InputSeverity.Error, "InputPathInvalid");
        }
        return directoryExists(full)
            ? new(InputSeverity.Valid, "InputPathFound")
            : new(InputSeverity.Error, "InputPathMissing");
    }

    // PHP wants an IANA zone ("Asia/Riyadh"); Windows ids ("Arab Standard Time") are rejected.
    public static InputCheck PhpTimezone(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0) return InputCheck.Ok;
        if (value.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return InputCheck.Ok;
        if (!value.Contains('/') || value.Any(char.IsWhiteSpace))
        {
            return new(InputSeverity.Error, "InputTimezoneFormat");
        }
        return TimeZoneInfo.TryConvertIanaIdToWindowsId(value, out _)
            ? InputCheck.Ok
            : new(InputSeverity.Warning, "InputTimezoneUnknown", value);
    }
}
