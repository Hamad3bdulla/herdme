using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;

namespace HerdMe.Windows.Services;

public static class AppLocalization
{
    private static readonly Lazy<ResourceLoader> Loader = new(() => new ResourceLoader(
        Path.Combine(AppContext.BaseDirectory, "HerdMe.Windows.pri"),
        "Resources"
    ));

    public static string LanguageTag => ApplicationLanguages.Languages.FirstOrDefault()
        ?? CultureInfo.CurrentUICulture.Name;

    public static FlowDirection LayoutDirection => UiLanguagePolicy.IsRightToLeft(LanguageTag)
        ? Microsoft.UI.Xaml.FlowDirection.RightToLeft
        : Microsoft.UI.Xaml.FlowDirection.LeftToRight;

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            var value = Loader.Value.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? key : value;
        }
        catch (Exception error) when (error is COMException or FileNotFoundException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"HerdMe could not resolve the localized resource '{key}': {error.Message}"
            );
            return key;
        }
    }

    public static string Format(string key, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
    }
}
