namespace HerdMe.Windows;

// A left click on the tray icon opens the quick panel (TrayPanelWindow.cs); the right-click
// menu stays as it was. During first-run setup there is nothing to control yet, so the click
// opens HerdMe instead.
public partial class App
{
    private static readonly TimeSpan TrayPanelReopenGuard = TimeSpan.FromMilliseconds(300);

    private TrayPanelWindow? trayPanel;

    private void TrayIcon_LeftClicked()
    {
        if (exitRequested || MainWindow is null) return;
        if (MainWindow.RequiresOnboarding)
        {
            ShowMainWindow();
            return;
        }
        trayPanel ??= new TrayPanelWindow(this);
        if (trayPanel.IsOpen)
        {
            trayPanel.HidePanel();
            return;
        }
        // The click that took focus away from the open panel already closed it.
        if (DateTimeOffset.UtcNow - trayPanel.HiddenAt < TrayPanelReopenGuard) return;
        trayPanel.ShowNear(BuildTrayPanelContent());
    }

    // Called with the tray status poll so an open panel follows the environment.
    private void RefreshTrayPanel()
    {
        if (exitRequested || trayPanel is not { IsOpen: true } panel) return;
        panel.Refresh(BuildTrayPanelContent());
    }

    private TrayPanelContent BuildTrayPanelContent()
    {
        var environment = services.Environment;
        return new TrayPanelContent(
            environment.IsRunning,
            environment.IsDegraded,
            KnownSites,
            MainWindow.UnseenMail,
            services.Shares.Active.Count,
            RecentSitePaths
        );
    }

    private void CloseTrayPanel()
    {
        trayPanel?.ClosePanel();
        trayPanel = null;
    }

    internal void ShowMainWindowFromPanel()
    {
        if (!exitRequested) ShowMainWindow();
    }

    internal void ShowPageFromPanel(string tag)
    {
        if (!exitRequested) ShowPageFromTray(tag);
    }

    internal void ShowSiteFromPanel(string path)
    {
        if (!exitRequested) ShowSiteFromTray(path);
    }

    internal void StopSharingFromPanel()
    {
        if (!exitRequested) StopSharingFromTray();
    }
}
