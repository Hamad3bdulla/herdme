using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;

namespace HerdMe.Windows.Services;

public static class AppLocalization
{
    private static readonly Lazy<ResourceLoader> Loader = new(() => new ResourceLoader(
        Path.Combine(AppContext.BaseDirectory, "HerdMe.Windows.pri")
    ));

    public static string LanguageTag => ApplicationLanguages.Languages.FirstOrDefault()
        ?? CultureInfo.CurrentUICulture.Name;

    public static FlowDirection LayoutDirection => UiLanguagePolicy.IsRightToLeft(LanguageTag)
        ? Microsoft.UI.Xaml.FlowDirection.RightToLeft
        : Microsoft.UI.Xaml.FlowDirection.LeftToRight;

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = Loader.Value.GetString(key);
        return string.IsNullOrWhiteSpace(value) ? key : value;
    }

    public static string Format(string key, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
    }
}
