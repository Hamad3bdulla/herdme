using Microsoft.UI.Xaml;

namespace HerdMe.Windows.Views;

internal enum StatusTone
{
    Neutral,
    Success,
    Caution,
    Critical
}

// Status visuals are defined as styles in Styles/DesignSystem.xaml. Assigning a style keeps
// its ThemeResource brushes live, so dots and pills follow light/dark/high-contrast changes.
internal static class StatusStyles
{
    public static Style Dot(StatusTone tone) => Find("StatusDot", tone);

    public static Style Pill(StatusTone tone) => Find("StatusPill", tone);

    public static Style Glyph(StatusTone tone) => Find("StatusGlyph", tone);

    private static Style Find(string prefix, StatusTone tone) =>
        (Style)Application.Current.Resources[$"{prefix}{tone}Style"];
}
