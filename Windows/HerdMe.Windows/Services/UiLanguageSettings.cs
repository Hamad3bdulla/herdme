namespace HerdMe.Windows.Services;

// The in-app language choice. HerdMe ships English and Arabic resources; anything else
// (including an old or hand-edited value) falls back to following Windows.
public static class UiLanguageSettings
{
    public const string System = "";
    public const string English = "en-US";
    public const string Arabic = "ar";

    public static readonly IReadOnlyList<string> Supported = [System, English, Arabic];

    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return System;
        var value = language.Trim();
        if (value.Equals(English, StringComparison.OrdinalIgnoreCase)
            || value.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            return English;
        }
        if (value.Equals(Arabic, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ar-", StringComparison.OrdinalIgnoreCase))
        {
            return Arabic;
        }
        return System;
    }
}
