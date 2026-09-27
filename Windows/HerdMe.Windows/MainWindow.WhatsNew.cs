using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;

namespace HerdMe.Windows;

// Shows What's new once after an upgrade. A new installation only records the version,
// and nothing is shown over onboarding or over the settings-load warning.
public sealed partial class MainWindow
{
    private string? pendingWhatsNewVersion;

    private void InitializeWhatsNew(string lastSeenVersion)
    {
        var current = services.Updates.CurrentVersion;
        if (string.Equals(lastSeenVersion, current, StringComparison.OrdinalIgnoreCase)) return;
        if (ChangelogReader.ShouldShow(lastSeenVersion, current)) pendingWhatsNewVersion = current;
        services.SiteSettings.UpdateLastSeenVersion(current);
        if (pendingWhatsNewVersion is not null && configurationLoadWarning is null)
        {
            Activated += ShowWhatsNewOnActivation;
        }
    }

    private async void ShowWhatsNewOnActivation(object sender, WindowActivatedEventArgs args)
    {
        Activated -= ShowWhatsNewOnActivation;
        await ShowPendingWhatsNewAsync();
    }

    private async Task ShowPendingWhatsNewAsync()
    {
        if (
            shuttingDown
            || RequiresOnboarding
            || pendingWhatsNewVersion is not { } version
            || Content is not FrameworkElement { XamlRoot: { } xamlRoot }
        ) return;
        pendingWhatsNewVersion = null;
        // Opened by itself only when there is something to read; About always offers it.
        if (WhatsNewDialog.Load(version).Count == 0) return;
        await WhatsNewDialog.ShowAsync(xamlRoot, version);
    }
}
