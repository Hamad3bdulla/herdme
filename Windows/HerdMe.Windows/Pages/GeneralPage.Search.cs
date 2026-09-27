using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

// Find a setting: sections whose title or rows match stay visible and open; the rest hide.
// Clearing the box puts every section back the way the user left it.
public sealed partial class GeneralPage
{
    private Dictionary<Expander, bool>? expandedBeforeSearch;

    private Expander[] SettingsSections =>
    [
        StartupSection,
        IntegrationSection,
        DomainsSection,
        UpdatesSection,
        CoreSection,
        RuntimeSection,
        ExtensionsSection
    ];

    private void SettingsSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        var query = sender.Text.Trim();
        if (query.Length == 0)
        {
            foreach (var section in SettingsSections)
            {
                section.Visibility = Visibility.Visible;
                if (expandedBeforeSearch?.TryGetValue(section, out var expanded) == true)
                {
                    section.IsExpanded = expanded;
                }
            }
            expandedBeforeSearch = null;
            SettingsSearchEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        expandedBeforeSearch ??= SettingsSections.ToDictionary(section => section, section => section.IsExpanded);
        var matches = 0;
        foreach (var section in SettingsSections)
        {
            var match = SectionMatches(section, query);
            section.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (!match) continue;
            section.IsExpanded = true;
            matches++;
        }
        SettingsSearchEmpty.Visibility = matches == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool SectionMatches(Expander section, string query) =>
        SettingsText(section.Header).Concat(SettingsText(section.Content)).Any(text =>
            text.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    // Walks the section's own element tree (collapsed content is not in the visual tree).
    private static IEnumerable<string> SettingsText(object? element)
    {
        switch (element)
        {
            case TextBlock text:
                if (!string.IsNullOrWhiteSpace(text.Text)) yield return text.Text;
                break;
            case string value:
                yield return value;
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    foreach (var text in SettingsText(child)) yield return text;
                }
                break;
            case Border border:
                foreach (var text in SettingsText(border.Child)) yield return text;
                break;
            case ToggleSwitch toggle:
                foreach (var text in SettingsText(toggle.Header)) yield return text;
                break;
            case ContentControl content:
                foreach (var text in SettingsText(content.Content)) yield return text;
                break;
        }
    }
}
