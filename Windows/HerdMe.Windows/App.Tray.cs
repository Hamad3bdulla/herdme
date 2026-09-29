using System.ComponentModel;
using System.Diagnostics;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;

namespace HerdMe.Windows;

// The live parts of the notification area: an icon and tooltip that follow the environment,
// Sites / PHP / Mail / Stop sharing entries in the tray menu, and the taskbar overlay badge.
public partial class App
{
    private const int TrayPhpRefreshTicks = 5;

    private static IReadOnlyList<SiteRecord> knownSites = [];
    private readonly DispatcherTimer trayStatusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private MenuFlyout? trayMenu;
    private MenuFlyoutSubItem? traySitesItem;
    private MenuFlyoutSubItem? trayPhpItem;
    private MenuFlyoutItem? trayMailItem;
    private MenuFlyoutItem? trayStopSharingItem;
    private MenuFlyoutItem? trayUpdatesItem;
    private TrayIconState? displayedTrayState;
    private string? displayedTrayTooltip;
    private string? trayMenuSignature;
    private TaskbarOverlayKind displayedOverlay;
    private IReadOnlyList<string> trayPhpCycles = [];
    private string trayPhpDefault = string.Empty;
    private int trayTicks;
    private int trayPhpRefreshing;

    // The last scanned site list (from the Sites page, the CLI, or the folder watcher).
    internal static IReadOnlyList<SiteRecord> KnownSites => Volatile.Read(ref knownSites);

    private static void RememberKnownSites(IReadOnlyList<SiteRecord> sites) =>
        Volatile.Write(ref knownSites, sites);

    // Called while the tray menu is built: the dynamic entries go right after "Open HerdMe".
    private void AddTrayDynamicItems(MenuFlyout menu)
    {
        trayMenu = menu;
        traySitesItem = new MenuFlyoutSubItem { Text = AppLocalization.Get("TraySitesMenuLabel") };
        trayPhpItem = new MenuFlyoutSubItem { Text = AppLocalization.Get("TrayPhpMenuLabel") };
        trayMailItem = new MenuFlyoutItem
        {
            Text = AppLocalization.Get("TrayMailLabel"),
            Command = TrayCommand(() => ShowPageFromTray("mail"))
        };
        trayStopSharingItem = new MenuFlyoutItem
        {
            Text = AppLocalization.Get("TrayStopSharingLabel"),
            Command = TrayCommand(StopSharingFromTray)
        };
        menu.Items.Insert(1, traySitesItem);
        menu.Items.Insert(2, trayPhpItem);
        menu.Items.Insert(3, trayMailItem);
        // Shown only while updates are waiting (App.Updates.cs).
        trayUpdatesItem = new MenuFlyoutItem
        {
            Text = AppLocalization.Get("TrayUpdatesLabel"),
            Command = TrayCommand(() => ShowPageFromTray("updates"))
        };
        RefreshTrayMenu();
    }

    private void StartTrayStatus()
    {
        trayStatusTimer.Tick += TrayStatusTimer_Tick;
        MainWindow.ActivityChanged += MainWindow_ActivityChanged;
        MainWindowVisibilityChanged += App_TrayVisibilityChanged;
        RefreshTrayPhpInBackground();
        UpdateTrayStatus();
        trayStatusTimer.Start();
    }

    private void StopTrayStatus()
    {
        trayStatusTimer.Stop();
        trayStatusTimer.Tick -= TrayStatusTimer_Tick;
        MainWindowVisibilityChanged -= App_TrayVisibilityChanged;
        if (MainWindow is not null) MainWindow.ActivityChanged -= MainWindow_ActivityChanged;
    }

    private void TrayStatusTimer_Tick(object? sender, object e)
    {
        if (exitRequested) return;
        trayTicks++;
        if (trayTicks % TrayPhpRefreshTicks == 0) RefreshTrayPhpInBackground();
        UpdateTrayStatus();
    }

    private void MainWindow_ActivityChanged(object? sender, EventArgs e)
    {
        if (exitRequested) return;
        UpdateTaskbarOverlay(force: false);
        RefreshTrayMenu();
    }

    // Windows drops the overlay while the taskbar button is gone (window hidden to the tray).
    private void App_TrayVisibilityChanged(object? sender, bool visible)
    {
        if (visible && !exitRequested) UpdateTaskbarOverlay(force: true);
    }

    private void UpdateTrayStatus()
    {
        if (exitRequested || trayIcon is null || MainWindow is null) return;
        var environment = services.Environment;
        var state = TrayPresentation.Choose(environment.IsRunning, environment.IsDegraded);
        if (displayedTrayState != state)
        {
            displayedTrayState = state;
            trayIcon.IconSource = new BitmapImage(new Uri(TrayPresentation.IconUri(state)));
        }
        var siteCount = KnownSites.Count;
        var tooltip = MainWindow.RequiresOnboarding
            ? AppLocalization.Get("TrayTooltipSetup")
            : state switch
            {
                TrayIconState.Running when siteCount > 0 => AppLocalization.Format("TrayTooltipRunning", siteCount),
                TrayIconState.Running => AppLocalization.Get("TrayTooltipRunningNoSites"),
                TrayIconState.Recovering => AppLocalization.Get("TrayTooltipRecovering"),
                _ => AppLocalization.Get("TrayTooltipStopped")
            };
        if (!string.Equals(displayedTrayTooltip, tooltip, StringComparison.Ordinal))
        {
            displayedTrayTooltip = tooltip;
            trayIcon.ToolTipText = tooltip;
        }
        RefreshTrayMenu();
        RefreshTrayPanel();
    }

    private void UpdateTaskbarOverlay(bool force)
    {
        if (MainWindow is null) return;
        var kind = TaskbarOverlay.Choose(MainWindow.UnseenMail, MainWindow.UnseenDumps, MainWindow.UnseenErrors);
        if (!force && kind == displayedOverlay) return;
        displayedOverlay = kind;
        var description = kind == TaskbarOverlayKind.Error
            ? AppLocalization.Format("TaskbarOverlayErrors", MainWindow.UnseenErrors)
            : AppLocalization.Format("TaskbarOverlayActivity", MainWindow.UnseenMail + MainWindow.UnseenDumps);
        TaskbarOverlay.Apply(WindowNative.GetWindowHandle(MainWindow), kind, description);
    }

    // Installed PHP versions come from disk, so they are read off the UI thread.
    private void RefreshTrayPhpInBackground()
    {
        if (exitRequested || Interlocked.Exchange(ref trayPhpRefreshing, 1) != 0) return;
        _ = Task.Run(() =>
        {
            IReadOnlyList<string> cycles;
            string current;
            try
            {
                cycles = services.PhpInstaller.InstalledCycles()
                    .Where(PhpRuntimeInstaller.IsSupportedCycle)
                    .ToList();
                current = services.RuntimePolicy.Load().PhpCycle;
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.Text.Json.JsonException)
            {
                Interlocked.Exchange(ref trayPhpRefreshing, 0);
                return;
            }
            var queued = MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref trayPhpRefreshing, 0);
                if (exitRequested) return;
                trayPhpCycles = cycles;
                trayPhpDefault = current;
                RefreshTrayMenu();
            }) ?? false;
            if (!queued) Interlocked.Exchange(ref trayPhpRefreshing, 0);
        });
    }

    // Rebuilds the dynamic entries only when what they show changed.
    private void RefreshTrayMenu()
    {
        if (exitRequested || trayMenu is null || traySitesItem is null || trayPhpItem is null
            || trayMailItem is null || trayStopSharingItem is null || trayUpdatesItem is null
            || MainWindow is null)
        {
            return;
        }
        var known = KnownSites;
        var recent = TrayPresentation.RecentSites(known, RecentSitePaths);
        var sites = TrayPresentation.MenuSites(known, recent);
        var shares = services.Shares.Active.Count;
        var unseenMail = MainWindow.UnseenMail;
        var pendingUpdates = MainWindow.PendingUpdates;
        var signature = string.Join(
            "\n",
            recent.Select(site => "recent:" + site.Path + "|" + site.Domain)
                .Concat(sites.Select(site => site.Path + "|" + site.Domain))
                .Append("php:" + trayPhpDefault + ":" + string.Join(",", trayPhpCycles))
                .Append("mail:" + unseenMail)
                .Append("shares:" + shares)
                .Append("updates:" + pendingUpdates)
        );
        if (string.Equals(signature, trayMenuSignature, StringComparison.Ordinal)) return;
        trayMenuSignature = signature;

        traySitesItem.Items.Clear();
        if (recent.Count > 0)
        {
            traySitesItem.Items.Add(new MenuFlyoutItem
            {
                Text = AppLocalization.Get("TraySitesRecentHeader"),
                IsEnabled = false
            });
            foreach (var site in recent) traySitesItem.Items.Add(TraySiteItem(site));
            if (sites.Count > 0) traySitesItem.Items.Add(new MenuFlyoutSeparator());
        }
        foreach (var site in sites) traySitesItem.Items.Add(TraySiteItem(site));
        var anySites = recent.Count > 0 || sites.Count > 0;
        if (anySites) traySitesItem.Items.Add(new MenuFlyoutSeparator());
        traySitesItem.Items.Add(new MenuFlyoutItem
        {
            Text = AppLocalization.Get(anySites ? "TraySitesShowAll" : "TraySitesEmpty"),
            Command = TrayCommand(() => ShowPageFromTray("sites"))
        });

        trayPhpItem.Items.Clear();
        foreach (var cycle in trayPhpCycles)
        {
            var version = cycle;
            trayPhpItem.Items.Add(new ToggleMenuFlyoutItem
            {
                Text = AppLocalization.Format("TrayPhpVersion", version),
                IsChecked = string.Equals(version, trayPhpDefault, StringComparison.Ordinal),
                Command = TrayCommand(() => SwitchPhpFromTray(version))
            });
        }
        if (trayPhpCycles.Count > 0) trayPhpItem.Items.Add(new MenuFlyoutSeparator());
        trayPhpItem.Items.Add(new MenuFlyoutItem
        {
            Text = AppLocalization.Get(trayPhpCycles.Count > 0 ? "TrayPhpManage" : "TrayPhpInstall"),
            Command = TrayCommand(() => ShowPageFromTray("php"))
        });

        trayMailItem.Text = unseenMail > 0
            ? AppLocalization.Format("TrayMailUnseenLabel", unseenMail)
            : AppLocalization.Get("TrayMailLabel");

        var sharingIndex = trayMenu.Items.IndexOf(trayStopSharingItem);
        if (shares > 0)
        {
            trayStopSharingItem.Text = AppLocalization.Format("TrayStopSharingLabel", shares);
            if (sharingIndex < 0) trayMenu.Items.Insert(trayMenu.Items.IndexOf(trayMailItem) + 1, trayStopSharingItem);
        }
        else if (sharingIndex >= 0)
        {
            trayMenu.Items.RemoveAt(sharingIndex);
        }

        var updatesIndex = trayMenu.Items.IndexOf(trayUpdatesItem);
        if (pendingUpdates > 0)
        {
            trayUpdatesItem.Text = AppLocalization.Format("TrayUpdatesCountLabel", pendingUpdates);
            if (updatesIndex < 0)
            {
                var anchor = trayMenu.Items.IndexOf(shares > 0 ? trayStopSharingItem : trayMailItem);
                trayMenu.Items.Insert(anchor + 1, trayUpdatesItem);
            }
        }
        else if (updatesIndex >= 0)
        {
            trayMenu.Items.RemoveAt(updatesIndex);
        }
    }

    private MenuFlyoutSubItem TraySiteItem(SiteRecord site)
    {
        var path = site.Path;
        var siteItem = new MenuFlyoutSubItem { Text = TrayPresentation.MenuText(site.Domain) };
        siteItem.Items.Add(new MenuFlyoutItem
        {
            Text = AppLocalization.Get("TraySiteOpenBrowser"),
            Command = TrayCommand(() => OpenSiteFromTray(path))
        });
        siteItem.Items.Add(new MenuFlyoutItem
        {
            Text = AppLocalization.Get("TraySiteShowInHerdMe"),
            Command = TrayCommand(() => ShowSiteFromTray(path))
        });
        return siteItem;
    }

    private XamlUICommand TrayCommand(Action action)
    {
        var command = new XamlUICommand();
        command.ExecuteRequested += (_, _) =>
        {
            if (exitRequested) return;
            action();
        };
        return command;
    }

    private static void ShowPageFromTray(string tag)
    {
        ShowMainWindow();
        MainWindow.NavigateToPage(tag);
    }

    private static void ShowSiteFromTray(string path)
    {
        ShowMainWindow();
        MainWindow.NavigateToSite(path);
    }

    internal void OpenSiteFromTray(string path)
    {
        var site = KnownSites.FirstOrDefault(candidate =>
            string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase));
        if (site is null) return;
        var environment = services.Environment;
        var uri = SitePresentation.SiteUri(site, environment.IsRunning, environment.HttpPort, environment.HttpsPort);
        try
        {
            // Handing a URL to the default browser needs the shell association.
            using var browser = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            MainWindow.RememberOpenedSite(site.Path);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            _ = DiagnosticLog.WriteFailureAsync(
                "tray",
                "open-site-failed",
                "HerdMe could not open the site in the browser.",
                error.ToString()
            );
        }
    }

    // Makes a PHP version the default and, when the environment is running, applies it
    // right away (the environment restarts PHP only if its configuration changed).
    private void SwitchPhpFromTray(string cycle)
    {
        if (string.Equals(cycle, trayPhpDefault, StringComparison.Ordinal)) return;
        trayPhpDefault = cycle;
        trayMenuSignature = null;
        RefreshTrayMenu();
        _ = backgroundTasks.RunAsync(cancellationToken => SwitchPhpAsync(cycle, cancellationToken));
    }

    // Dashboard "Global PHP version": the same switch as the tray, even when the tray still
    // shows that cycle (the PHP page may have changed the default without touching the tray).
    internal void SwitchDefaultPhp(string cycle)
    {
        if (string.IsNullOrWhiteSpace(cycle)) return;
        trayPhpDefault = cycle;
        trayMenuSignature = null;
        RefreshTrayMenu();
        _ = backgroundTasks.RunAsync(cancellationToken => SwitchPhpAsync(cycle, cancellationToken));
    }

    private async Task SwitchPhpAsync(string cycle, CancellationToken cancellationToken)
    {
        try
        {
            var settings = services.RuntimePolicy.Load();
            settings.PhpCycle = cycle;
            services.RuntimePolicy.Save(settings);
            services.UserPath.Synchronize(services.ComposerTools.CommandLineDirectories(cycle));
            if (services.Environment.IsRunning || services.Environment.IsDegraded)
            {
                var sites = await ScanSitesAsync(cancellationToken);
                RequestJumpListRefresh(sites);
                await services.Environment.SynchronizeSitesAsync(sites, cancellationToken);
            }
            if (exitRequested) return;
            await RunOnUiAsync(() =>
            {
                MainWindow.DiscardCachedPage("php");
                if (IsMainWindowVisible)
                {
                    MainWindow.ShowToast(AppLocalization.Format("TrayPhpSwitchedToast", cycle));
                }
                RefreshTrayPhpInBackground();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // HerdMe is exiting.
        }
        catch (Exception error)
        {
            _ = DiagnosticLog.WriteFailureAsync(
                "tray",
                "php-switch-failed",
                "HerdMe could not switch the default PHP version from the tray.",
                error.ToString()
            );
            // Put the check mark back on the version that is really saved.
            if (!exitRequested) MainWindow.DispatcherQueue.TryEnqueue(RefreshTrayPhpInBackground);
        }
    }

    private void StopSharingFromTray()
    {
        _ = backgroundTasks.RunAsync(_ => services.Shares.StopAllAsync());
    }
}
