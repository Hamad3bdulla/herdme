using System.Collections.ObjectModel;
using System.Diagnostics;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

public sealed partial class DebuggerPage : Page
{
    private readonly CoreClient coreClient;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly PhpRuntimeInstaller runtimeInstaller;
    private readonly XdebugManager xdebugManager;
    private readonly ManagedComponentUpdateManager componentUpdateManager;
    private readonly SiteConfigurationStore siteSettings;
    private readonly WindowsLocalEnvironment environment;
    private readonly XdebugProfileStore profileStore = new();
    private PhpRuntimeSettings settings;
    private string? phpExecutable;

    public ObservableCollection<SiteRecord> Sites { get; } = [];

    public ObservableCollection<DebuggerProfileRow> Profiles { get; } = [];

    public DebuggerPage(
        CoreClient coreClient,
        PhpRuntimePolicy runtimePolicy,
        PhpRuntimeInstaller runtimeInstaller,
        XdebugManager xdebugManager,
        ManagedComponentUpdateManager componentUpdateManager,
        SiteConfigurationStore siteSettings,
        WindowsLocalEnvironment environment
    )
    {
        this.coreClient = coreClient;
        this.runtimePolicy = runtimePolicy;
        this.runtimeInstaller = runtimeInstaller;
        this.xdebugManager = xdebugManager;
        this.componentUpdateManager = componentUpdateManager;
        this.siteSettings = siteSettings;
        this.environment = environment;
        InitializeComponent();
        settings = runtimePolicy.Load();
        ApplySettings();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        await RefreshSitesAsync();
        await RefreshProfilesAsync();
    }

    private async Task RefreshAsync()
    {
        InstallProgress.IsActive = true;
        InstallStatusText.Text = AppLocalization.Get("DebuggerChecking");
        InstallButton.IsEnabled = false;
        try
        {
            phpExecutable = runtimeInstaller.IsInstalled(settings.PhpCycle)
                ? runtimeInstaller.PhpExecutable(settings.PhpCycle)
                : null;
            if (phpExecutable is null)
            {
                InstallStatusText.Text = AppLocalization.Get("DebuggerPhpUnavailable");
                EnabledToggle.IsEnabled = false;
                return;
            }

            settings.PhpCycle = await xdebugManager.PhpCycleAsync(phpExecutable);
            var installation = await xdebugManager.InstalledAsync(
                phpExecutable,
                settings.PhpCycle
            );
            ExtensionPathText.Text = xdebugManager.ExtensionPath(settings.PhpCycle);
            InstallStatusText.Text = installation is null
                ? AppLocalization.Get("DebuggerNotInstalled")
                : AppLocalization.Format("DebuggerVersion", installation.Version);
            var update = installation is null
                ? null
                : componentUpdateManager.LatestUpdate($"xdebug:{settings.PhpCycle}");
            if (installation is not null && update is not null
                && !RuntimeVersionComparison.IsNewer(
                    update.LatestVersion,
                    installation.Version
                ))
            {
                update = null;
            }
            if (installation is not null && update is null)
            {
                try
                {
                    var release = await xdebugManager.ResolveReleaseAsync(phpExecutable);
                    if (RuntimeVersionComparison.IsNewer(
                            release.Version,
                            installation.Version
                        ))
                    {
                        update = new ManagedComponentUpdate(
                            $"xdebug:{settings.PhpCycle}",
                            $"Xdebug (PHP {settings.PhpCycle})",
                            installation.Version,
                            release.Version,
                            "debugger"
                        );
                    }
                }
                catch (Exception)
                {
                }
            }
            if (update is not null)
            {
                InstallStatusText.Text = AppLocalization.Format(
                    "DebuggerUpdateAvailable",
                    update.LatestVersion
                );
            }
            InstallButtonText.Text = AppLocalization.Get(
                update is null ? "CommonInstall" : "DebuggerUpdateButton"
            );
            InstallButton.IsEnabled = installation is null || update is not null;
            EnabledToggle.IsEnabled = installation is not null;
            if (installation is null)
            {
                EnabledToggle.IsOn = false;
                settings.Debugger.Enabled = false;
            }
        }
        catch (Exception error)
        {
            InstallStatusText.Text = error.Message;
            EnabledToggle.IsEnabled = false;
        }
        finally
        {
            InstallProgress.IsActive = false;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (phpExecutable is null) return;
        InstallProgress.IsActive = true;
        InstallButton.IsEnabled = false;
        InstallStatusText.Text = AppLocalization.Get("DebuggerInstalling");
        try
        {
            var installation = await xdebugManager.InstallAsync(phpExecutable);
            settings.PhpCycle = await xdebugManager.PhpCycleAsync(phpExecutable);
            ExtensionPathText.Text = installation.ExtensionPath;
            InstallStatusText.Text = AppLocalization.Format(
                "DebuggerVersion",
                installation.Version
            );
            InstallButtonText.Text = AppLocalization.Get("CommonInstall");
            EnabledToggle.IsEnabled = true;
        }
        catch (Exception error)
        {
            InstallStatusText.Text = error.Message;
            InstallButton.IsEnabled = true;
        }
        finally
        {
            InstallProgress.IsActive = false;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        settings.Debugger.Enabled = EnabledToggle.IsOn;
        settings.Debugger.DetectBreakpoints = TriggerToggle.IsOn;
        settings.Debugger.Port = double.IsNaN(PortBox.Value) ? 9_003 : (int)PortBox.Value;
        settings.Debugger.IdeKey = IdeKeyBox.Text;
        settings.Debugger.ProfilerEnabled = ProfilerToggle.IsOn;
        try
        {
            settings = PhpRuntimePolicy.Normalize(settings);
            _ = PhpRuntimePolicy.BuildPhpOptions(settings);
            runtimePolicy.Save(settings);
            ApplySettings();
            SaveStatusText.Text = AppLocalization.Get("DebuggerSavedForNextStart");
            UpdateSessionState();
        }
        catch (Exception error)
        {
            EnabledToggle.IsOn = false;
            settings.Debugger.Enabled = false;
            SaveStatusText.Text = error.Message;
        }
    }

    private void ApplySettings()
    {
        EnabledToggle.IsOn = settings.Debugger.Enabled;
        TriggerToggle.IsOn = settings.Debugger.DetectBreakpoints;
        PortBox.Value = settings.Debugger.Port;
        IdeKeyBox.Text = settings.Debugger.IdeKey;
        ProfilerToggle.IsOn = settings.Debugger.ProfilerEnabled;
        EndpointText.Text = AppLocalization.Format(
            "DebuggerIdeEndpoint",
            settings.Debugger.Port
        );
    }

    private async Task RefreshSitesAsync()
    {
        SessionProgress.IsActive = true;
        SessionStatusText.Text = AppLocalization.Get("DebuggerScanningSites");
        try
        {
            var siteConfiguration = siteSettings.Load();
            var scanned = await coreClient.ScanAsync(
                siteConfiguration.Roots,
                siteConfiguration.Tld,
                siteConfiguration.LinkedSites
            );
            Sites.Clear();
            foreach (var site in scanned) Sites.Add(site);
            SiteBox.SelectedIndex = Sites.Count > 0 ? 0 : -1;
            UpdateSessionState();
        }
        catch (Exception error)
        {
            SessionStatusText.Text = error.Message;
            StartSessionButton.IsEnabled = false;
            ProfileRequestButton.IsEnabled = false;
        }
        finally
        {
            SessionProgress.IsActive = false;
        }
    }

    private void SiteBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSessionState();
    }

    private void StartSession_Click(object sender, RoutedEventArgs e)
    {
        if (SiteBox.SelectedItem is not SiteRecord site) return;
        if (!settings.Debugger.Enabled)
        {
            SessionStatusText.Text = AppLocalization.Get("DebuggerEnableAndSave");
            return;
        }
        if (!environment.IsRunning)
        {
            SessionStatusText.Text = AppLocalization.Get("DebuggerStartEnvironment");
            return;
        }

        try
        {
            var uri = SitePresentation.DebugUri(
                site,
                true,
                environment.HttpPort,
                environment.HttpsPort,
                settings.Debugger.IdeKey
            );
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            SessionStatusText.Text = AppLocalization.Format("DebuggerOpenedSite", site.Domain);
        }
        catch (Exception error)
        {
            SessionStatusText.Text = error.Message;
        }
    }

    private void UpdateSessionState()
    {
        var hasSite = SiteBox.SelectedItem is SiteRecord;
        StartSessionButton.IsEnabled = hasSite && settings.Debugger.Enabled;
        ProfileRequestButton.IsEnabled = hasSite
            && settings.Debugger.Enabled
            && settings.Debugger.ProfilerEnabled;
        SessionStatusText.Text = Sites.Count == 0
            ? AppLocalization.Get("DebuggerNoSites")
            : environment.IsRunning
                ? settings.Debugger.Enabled
                    ? AppLocalization.Get("DebuggerReady")
                    : AppLocalization.Get("DebuggerEnableToStart")
                : AppLocalization.Get("DebuggerStartEnvironment");
    }

    private async void ProfileRequest_Click(object sender, RoutedEventArgs e)
    {
        if (SiteBox.SelectedItem is not SiteRecord site) return;
        if (!settings.Debugger.Enabled || !settings.Debugger.ProfilerEnabled)
        {
            SessionStatusText.Text = AppLocalization.Get("DebuggerEnableProfilerAndSave");
            return;
        }
        if (!environment.IsRunning)
        {
            SessionStatusText.Text = AppLocalization.Get("DebuggerStartEnvironment");
            return;
        }

        try
        {
            var uri = SitePresentation.ProfileUri(
                site,
                true,
                environment.HttpPort,
                environment.HttpsPort,
                settings.Debugger.IdeKey
            );
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            SessionStatusText.Text = AppLocalization.Format("DebuggerProfilingSite", site.Domain);
            // Xdebug finishes the file when the request ends; pick it up shortly after.
            await Task.Delay(TimeSpan.FromSeconds(3));
            await RefreshProfilesAsync();
        }
        catch (Exception error)
        {
            SessionStatusText.Text = error.Message;
        }
    }

    private async void RefreshProfiles_Click(object sender, RoutedEventArgs e)
    {
        await RefreshProfilesAsync();
    }

    private async Task RefreshProfilesAsync()
    {
        RefreshProfilesButton.IsEnabled = false;
        try
        {
            var files = await Task.Run(() => profileStore.List());
            Profiles.Clear();
            foreach (var file in files) Profiles.Add(new DebuggerProfileRow(file));
            ProfilesStatusText.Text = files.Count == 0
                ? AppLocalization.Get("DebuggerProfilesEmpty")
                : AppLocalization.Format("DebuggerProfilesCount", files.Count);
            DeleteProfilesButton.IsEnabled = files.Count > 0;
        }
        catch (Exception error)
        {
            ProfilesStatusText.Text = error.Message;
        }
        finally
        {
            RefreshProfilesButton.IsEnabled = true;
        }
    }

    private void OpenProfiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(profileStore.Directory);
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(profileStore.Directory);
            Process.Start(startInfo);
        }
        catch (Exception error)
        {
            ProfilesStatusText.Text = error.Message;
        }
    }

    private async void DeleteProfiles_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Get("DebuggerDeleteProfilesTitle"),
            Content = AppLocalization.Get("DebuggerDeleteProfilesMessage"),
            PrimaryButtonText = AppLocalization.Get("DebuggerDeleteProfilesConfirm"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await Task.Run(() => profileStore.DeleteAll());
        await RefreshProfilesAsync();
    }

    private async void ProfilesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DebuggerProfileRow row) return;
        ProfilesStatusText.Text = AppLocalization.Format("DebuggerProfileLoading", row.File.Name);
        CachegrindProfile profile;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            profile = await Task.Run(() => profileStore.Load(row.File.Path, cancellation.Token));
        }
        catch (Exception error)
        {
            ProfilesStatusText.Text = AppLocalization.Format("DebuggerProfileFailed", error.Message);
            return;
        }
        ProfilesStatusText.Text = AppLocalization.Format("DebuggerProfilesCount", Profiles.Count);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format(
                "DebuggerProfileTitle",
                FormatDuration(profile.TotalTime),
                FormatBytes(profile.PeakMemory)
            ),
            Content = ProfileView(profile),
            PrimaryButtonText = AppLocalization.Get("DebuggerProfileDelete"),
            CloseButtonText = AppLocalization.Get("CommonClose"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await Task.Run(() => profileStore.Delete(row.File.Path));
        }
        catch (Exception error)
        {
            ProfilesStatusText.Text = error.Message;
            return;
        }
        await RefreshProfilesAsync();
    }

    private static UIElement ProfileView(CachegrindProfile profile)
    {
        const int shown = 25;
        var panel = new StackPanel { Spacing = 8, Width = 640 };
        if (!string.IsNullOrWhiteSpace(profile.Command))
        {
            panel.Children.Add(new TextBlock
            {
                Text = profile.Command,
                TextWrapping = TextWrapping.Wrap,
                FlowDirection = FlowDirection.LeftToRight,
                Style = StatusStyles.Text(StatusTone.Neutral)
            });
        }
        var table = new Grid { ColumnSpacing = 12, RowSpacing = 4, FlowDirection = FlowDirection.LeftToRight };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var column = 0; column < 4; column++)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        string[] headers =
        [
            AppLocalization.Get("DebuggerProfileFunction"),
            AppLocalization.Get("DebuggerProfileCalls"),
            AppLocalization.Get("DebuggerProfileSelf"),
            AppLocalization.Get("DebuggerProfileInclusive"),
            AppLocalization.Get("DebuggerProfileMemory")
        ];
        AddRow(table, 0, headers, true);
        var functions = profile.Functions.Take(shown).ToArray();
        for (var index = 0; index < functions.Length; index++)
        {
            var function = functions[index];
            AddRow(
                table,
                index + 1,
                [
                    function.Name,
                    function.Calls.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
                    FormatDuration(function.SelfTime),
                    FormatDuration(function.InclusiveTime),
                    FormatBytes(function.InclusiveMemory)
                ],
                false
            );
        }
        var scroller = new ScrollViewer
        {
            Content = table,
            MaxHeight = 420,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto
        };
        panel.Children.Add(scroller);
        if (profile.Functions.Count > shown)
        {
            panel.Children.Add(new TextBlock
            {
                Text = AppLocalization.Format("DebuggerProfileMore", profile.Functions.Count - shown),
                Style = StatusStyles.Text(StatusTone.Neutral)
            });
        }
        return panel;
    }

    private static void AddRow(Grid table, int row, string[] cells, bool header)
    {
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var column = 0; column < cells.Length; column++)
        {
            var text = new TextBlock
            {
                Text = cells[column],
                FontWeight = header ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                TextTrimming = column == 0 ? TextTrimming.CharacterEllipsis : TextTrimming.None,
                HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right
            };
            if (column == 0 && !header)
            {
                text.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");
                ToolTipService.SetToolTip(text, cells[column]);
            }
            Grid.SetRow(text, row);
            Grid.SetColumn(text, column);
            table.Children.Add(text);
        }
    }

    private static string FormatDuration(TimeSpan value)
    {
        return value.TotalMilliseconds >= 1_000
            ? AppLocalization.Format("DebuggerSeconds", value.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture))
            : AppLocalization.Format("DebuggerMilliseconds", value.TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture));
    }

    private static string FormatBytes(long bytes)
    {
        var megabytes = Math.Abs(bytes) / 1_048_576d;
        return megabytes >= 1
            ? AppLocalization.Format("DebuggerMegabytes", megabytes.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture))
            : AppLocalization.Format("DebuggerKilobytes", (Math.Abs(bytes) / 1_024d).ToString("0", System.Globalization.CultureInfo.CurrentCulture));
    }
}

public sealed class DebuggerProfileRow(XdebugProfileFile file)
{
    public XdebugProfileFile File { get; } = file;

    public string Label { get; } = AppLocalization.Format(
        "DebuggerProfileRow",
        file.CreatedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
        (file.Bytes / 1_024d).ToString("N0", System.Globalization.CultureInfo.CurrentCulture)
    );
}
