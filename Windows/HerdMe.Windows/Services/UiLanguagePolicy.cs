using System.Globalization;

namespace HerdMe.Windows.Services;

public static class UiLanguagePolicy
{
    public static bool IsRightToLeft(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            return CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;
        }

        try
        {
            return CultureInfo.GetCultureInfo(languageTag).TextInfo.IsRightToLeft;
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;
        }
    }
}
