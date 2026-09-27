using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace HerdMe.Windows;

// Start all / Stop all / Open site under the HerdMe thumbnail on the taskbar. Like the tray
// menu, Stop all does not ask first: the button is only reachable on purpose.
public sealed partial class MainWindow
{
    private TaskbarThumbnailToolbar? thumbnailToolbar;
    private string? thumbnailSignature;

    private void InitializeThumbnailToolbar()
    {
        var handle = WindowNative.GetWindowHandle(this);
        if (handle == IntPtr.Zero) return;
        thumbnailToolbar = new TaskbarThumbnailToolbar(handle);
        thumbnailToolbar.Clicked += ThumbnailToolbar_Clicked;
        UpdateThumbnailToolbar();
    }

    private void ReleaseThumbnailToolbar()
    {
        if (thumbnailToolbar is null) return;
        thumbnailToolbar.Clicked -= ThumbnailToolbar_Clicked;
        thumbnailToolbar.Dispose();
        thumbnailToolbar = null;
    }

    // Called with the title bar status poll, so it follows the environment every 2 s while
    // the window (and so its taskbar button) is visible.
    private void UpdateThumbnailToolbar()
    {
        if (shuttingDown || thumbnailToolbar is null) return;
        var environment = services.Environment;
        var site = ThumbnailSite();
        var buttons = TaskbarThumbnailButtons.For(
            environment.IsRunning,
            environment.IsDegraded,
            busy: RequiresOnboarding,
            hasSite: site is not null
        );
        var openTooltip = site is null
            ? AppLocalization.Get("TaskbarThumbOpenSiteNone")
            : AppLocalization.Format("TaskbarThumbOpenSite", site.Domain);
        var signature = TaskbarThumbnailButtons.Signature(buttons, openTooltip);
        if (string.Equals(signature, thumbnailSignature, StringComparison.Ordinal)) return;
        thumbnailSignature = signature;
        thumbnailToolbar.Update(buttons
            .Select(button => (button, button.Button == ThumbnailButton.OpenSite
                ? openTooltip
                : AppLocalization.Get(button.TooltipKey)))
            .ToList());
    }

    // The site opened last in HerdMe, else the first favorite or site HerdMe knows.
    private Models.SiteRecord? ThumbnailSite()
    {
        var sites = App.KnownSites;
        if (sites.Count == 0) return null;
        if (LastOpenedSitePath is { } last
            && sites.FirstOrDefault(site => string.Equals(site.Path, last, StringComparison.OrdinalIgnoreCase)) is { } opened)
        {
            return opened;
        }
        return TrayPresentation.MenuSites(sites).FirstOrDefault();
    }

    // Raised from the window procedure; the work is queued so the taskbar is not kept waiting.
    private void ThumbnailToolbar_Clicked(object? sender, ThumbnailButton button)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (shuttingDown) return;
            var app = (App)Application.Current;
            switch (button)
            {
                case ThumbnailButton.StartAll:
                    await app.StartAllAsync();
                    break;
                case ThumbnailButton.StopAll:
                    await app.StopAllAsync();
                    break;
                case ThumbnailButton.OpenSite when ThumbnailSite() is { } site:
                    app.OpenSiteFromTray(site.Path);
                    break;
            }
            if (shuttingDown) return;
            UpdateTitleBarStatus();
            UpdateThumbnailToolbar();
        });
    }
}
