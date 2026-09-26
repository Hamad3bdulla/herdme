using System.Globalization;

namespace HerdMe.Windows.Services;

/// <summary>
/// Localizes user-visible service text. The app sets <see cref="Localize"/> to its resource
/// lookup; when it is unset or returns null the English text is used, which keeps services and
/// contract tests independent of WinUI resources.
/// </summary>
public static class ServiceText
{
    public static Func<string, string?>? Localize { get; set; }

    public static string Get(string key, string english) => Localize?.Invoke(key) ?? english;

    public static string Format(string key, string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key, englishFormat), args);
}
