using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HerdMe.Windows.Pages;

public sealed partial class SitesPage : Page
{
    private readonly CoreClient coreClient = new();
    private readonly WindowsLocalEnvironment environment = AppServices.Environment;
    private readonly SiteConfigurationStore settingsStore = AppServices.SiteSettings;
    private readonly LaravelProjectCreator projectCreator = new();
    private readonly SiteRuntimeStore siteRuntimeStore = new();
    private readonly PhpRuntimeInstaller phpInstaller = new();
    private readonly NodeRuntimeInstaller nodeInstaller = new();
    private bool loaded;
    private bool suppressPreviewToggle;
    private SiteRecord? selectedSite;

    public ObservableCollection<string> Roots { get; } = [];
    public ObservableCollection<SiteRecord> Sites { get; } = [];
    public ObservableCollection<SiteRecord> VisibleSites { get; } = [];

    public SitesPage()
    {
        InitializeComponent();
        var settings = settingsStore.Load();
        foreach (var root in settings.Roots) Roots.Add(root);
        Directory.CreateDirectory(Roots[0]);
        RootPathTextBox.Text = Roots[0];
        TldTextBox.Text = settings.Tld;
        suppressPreviewToggle = true;
        PreviewToggle.IsOn = settings.ShowPreviews;
        suppressPreviewToggle = false;
        UpdateEnvironmentState();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        await ScanAsync();
    }

    private async void Environment_Click(object sender, RoutedEventArgs e)
    {
        EnvironmentButton.IsEnabled = false;
        try
        {
            if (environment.IsRunning)
            {
                EnvironmentStatusText.Text = "Stopping";
                await environment.StopAsync();
                SaveSettings(startAutomatically: false);
            }
            else
            {
                if (Sites.Count == 0) await ScanAsync();
                EnvironmentStatusText.Text = "Starting";
                await environment.StartAsync(Sites);
                SaveSettings(startAutomatically: true);
            }
        }
        catch (Exception error)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "HerdMe",
                Content = error.Message,
                CloseButtonText = "OK"
            };
            await dialog.ShowAsync();
        }
        finally
        {
            EnvironmentButton.IsEnabled = true;
            UpdateEnvironmentState();
            _ = RefreshPreviewAsync();
        }
    }

    private void UpdateEnvironmentState()
    {
        var running = environment.IsRunning;
        EnvironmentStatusText.Text = running ? "Running" : "Stopped";
        EnvironmentButtonText.Text = running ? "Stop all" : "Start all";
        EnvironmentButtonIcon.Symbol = running ? Symbol.Stop : Symbol.Play;
        EnvironmentEndpointText.Text = running
            ? environment.HttpsPort is not null ? "HTTPS" : "HTTP"
            : string.Empty;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            RootPathTextBox.Text = folder.Path;
        }
    }

    private async void LinkSite_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        if (SiteConfigurationStore.BelongsToOtherHerd(folder.Path))
        {
            await ShowErrorAsync(
                "HerdMe does not link projects inside another application's folders. Choose a HerdMe-owned folder instead."
            );
            return;
        }
        var settings = settingsStore.Load();
        if (!settings.LinkedSites.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
        {
            settings.LinkedSites.Add(folder.Path);
            settingsStore.Save(settings);
        }
        await ScanAsync();
    }

    private async void UnlinkSelectedSite_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { Linked: true } site) return;
        var settings = settingsStore.Load();
        settings.LinkedSites.RemoveAll(path => path.Equals(site.Path, StringComparison.OrdinalIgnoreCase));
        settingsStore.Save(settings);
        await ScanAsync();
    }

    private async void CreateLaravel_Click(object sender, RoutedEventArgs e)
    {
        var parent = RootList.SelectedItem as string ?? Roots.FirstOrDefault();
        if (parent is null)
        {
            await ShowErrorAsync("Add a site folder before creating a Laravel project.");
            return;
        }
        var nameBox = new TextBox { Header = "Project name", PlaceholderText = "my-app" };
        var starterBox = new ComboBox
        {
            Header = "Starter kit",
            ItemsSource = new[] { "None", "React", "Vue", "Svelte", "Livewire", "Custom" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var customStarterBox = new TextBox
        {
            Header = "Custom Composer package",
            PlaceholderText = "vendor/package",
            Visibility = Visibility.Collapsed
        };
        starterBox.SelectionChanged += (_, _) =>
        {
            customStarterBox.Visibility = starterBox.SelectedItem?.ToString() == "Custom"
                ? Visibility.Visible
                : Visibility.Collapsed;
        };
        var testingBox = new ComboBox
        {
            Header = "Testing framework",
            ItemsSource = new[] { "Pest", "PHPUnit" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var boostToggle = new ToggleSwitch { Header = "Install Laravel Boost", IsOn = true };
        var gitToggle = new ToggleSwitch { Header = "Initialize Git repository" };
        var content = new StackPanel { Spacing = 12, MinWidth = 380 };
        content.Children.Add(new TextBlock
        {
            Text = $"Location: {parent}",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        content.Children.Add(nameBox);
        content.Children.Add(starterBox);
        content.Children.Add(customStarterBox);
        content.Children.Add(testingBox);
        content.Children.Add(boostToggle);
        content.Children.Add(gitToggle);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Create Laravel project",
            Content = content,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var request = new LaravelProjectRequest(
            nameBox.Text,
            parent,
            starterBox.SelectedItem?.ToString() ?? "None",
            testingBox.SelectedItem?.ToString() ?? "Pest",
            boostToggle.IsOn,
            gitToggle.IsOn,
            starterBox.SelectedItem?.ToString() == "Custom" ? customStarterBox.Text : null
        );
        var stages = LaravelProjectCreationStages.For(request);
        var statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = LaravelProjectCreationStages.Detail(stages[0])
        };
        var stageRows = new Dictionary<LaravelProjectCreationStage, TextBlock>();
        var progressContent = new StackPanel { Spacing = 10, MinWidth = 420 };
        var progressRing = new ProgressRing { IsActive = true, Width = 20, Height = 20 };
        var progressHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        progressHeader.Children.Add(progressRing);
        progressHeader.Children.Add(statusText);
        progressContent.Children.Add(progressHeader);
        foreach (var stage in stages)
        {
            var row = new TextBlock { TextWrapping = TextWrapping.Wrap };
            stageRows.Add(stage, row);
            progressContent.Children.Add(row);
        }
        var progressDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Creating Laravel project",
            Content = new ScrollViewer
            {
                Content = progressContent,
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Auto
            }
        };
        var currentStage = stages[0];
        UpdateProjectCreationProgress(stages, stageRows, statusText, currentStage);
        var creationProgress = new Progress<LaravelProjectCreationStage>(stage =>
        {
            currentStage = stage;
            UpdateProjectCreationProgress(stages, stageRows, statusText, stage);
        });

        CreateLaravelButton.IsEnabled = false;
        ScanProgress.IsActive = true;
        var progressDialogOperation = progressDialog.ShowAsync();
        await Task.Yield();
        try
        {
            await projectCreator.CreateAsync(request, creationProgress);
            currentStage = LaravelProjectCreationStage.RegisteringSite;
            UpdateProjectCreationProgress(stages, stageRows, statusText, currentStage);
            await ScanAsync(throwOnError: true);
            currentStage = LaravelProjectCreationStage.Completed;
            UpdateProjectCreationProgress(stages, stageRows, statusText, currentStage);
            progressRing.IsActive = false;
            progressDialog.Title = "Laravel project created";
            progressDialog.CloseButtonText = "Done";
        }
        catch (Exception error)
        {
            UpdateProjectCreationFailure(stageRows, currentStage);
            progressRing.IsActive = false;
            progressDialog.Title = "Laravel project could not be created";
            var failure = CommandErrorPresenter.Present(
                error.Message,
                "Laravel Installer could not finish creating the site."
            );
            statusText.Text = failure.Message;
            if (failure.TechnicalDetails is not null)
            {
                progressContent.Children.Add(new Expander
                {
                    Header = "Technical details",
                    IsExpanded = false,
                    Content = new ScrollViewer
                    {
                        MaxHeight = 160,
                        Content = new TextBlock
                        {
                            Text = failure.TechnicalDetails,
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true
                        }
                    }
                });
            }
            progressDialog.CloseButtonText = "Close";
        }
        finally
        {
            ScanProgress.IsActive = false;
            CreateLaravelButton.IsEnabled = true;
        }
        await progressDialogOperation;
    }

    private static void UpdateProjectCreationProgress(
        IReadOnlyList<LaravelProjectCreationStage> stages,
        IReadOnlyDictionary<LaravelProjectCreationStage, TextBlock> rows,
        TextBlock statusText,
        LaravelProjectCreationStage current
    )
    {
        var currentIndex = 0;
        while (currentIndex < stages.Count && stages[currentIndex] != current) currentIndex++;
        for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index];
            var prefix = index < currentIndex ? "Completed" : index == currentIndex ? "In progress" : "Pending";
            rows[stage].Text = $"{prefix}: {LaravelProjectCreationStages.Title(stage)}";
            rows[stage].FontWeight = index == currentIndex
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
        }
        statusText.Text = LaravelProjectCreationStages.Detail(current);
    }

    private static void UpdateProjectCreationFailure(
        IReadOnlyDictionary<LaravelProjectCreationStage, TextBlock> rows,
        LaravelProjectCreationStage current
    )
    {
        rows[current].Text = $"Failed: {LaravelProjectCreationStages.Title(current)}";
        rows[current].FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
    }

    private async void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var path = RootPathTextBox.Text.Trim();
        if (path.Length > 0 && SiteConfigurationStore.BelongsToOtherHerd(path))
        {
            await ShowErrorAsync(
                "HerdMe does not park another application's folders. Choose a HerdMe-owned folder instead."
            );
            return;
        }
        if (path.Length > 0 && !Roots.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            Roots.Add(Path.GetFullPath(path));
            SaveSettings();
            await ScanAsync();
        }
    }

    private async void EditRuntimes_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is null) return;
        await ConfigureSiteAsync(selectedSite);
    }

    private async Task ConfigureSiteAsync(SiteRecord site)
    {
        var path = site.Path;
        var phpDefault = "Default";
        var phpVersions = new List<string> { phpDefault };
        phpVersions.AddRange(phpInstaller.InstalledCycles());
        var nodeDefault = "Default";
        var nodeVersions = new List<string> { nodeDefault };
        nodeVersions.AddRange(nodeInstaller.InstalledVersions());
        var phpBox = new ComboBox
        {
            Header = "PHP",
            ItemsSource = phpVersions,
            SelectedItem = site.PhpVersion is not null && phpVersions.Contains(site.PhpVersion)
                ? site.PhpVersion
                : phpDefault,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var nodeBox = new ComboBox
        {
            Header = "Node.js",
            ItemsSource = nodeVersions,
            SelectedItem = site.NodeVersion is not null && nodeVersions.Contains(site.NodeVersion)
                ? site.NodeVersion
                : nodeDefault,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var content = new StackPanel { Spacing = 12, MinWidth = 340 };
        content.Children.Add(phpBox);
        content.Children.Add(nodeBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = site.Name,
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var wasRunning = environment.IsRunning;
        try
        {
            if (wasRunning) await environment.StopAsync();
            var php = phpBox.SelectedItem?.ToString();
            var node = nodeBox.SelectedItem?.ToString();
            siteRuntimeStore.SetPhp(path, php == phpDefault ? null : php);
            siteRuntimeStore.SetNode(path, node == nodeDefault ? null : node);
            await ScanAsync();
            if (wasRunning) await environment.StartAsync(Sites);
        }
        catch (Exception error)
        {
            await ShowErrorAsync(error.Message);
        }
        finally
        {
            UpdateEnvironmentState();
        }
    }

    private async void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (RootList.SelectedItem is string path)
        {
            Roots.Remove(path);
            SaveSettings();
            if (Roots.Count == 0)
            {
                var fallback = settingsStore.Load().Roots[0];
                Roots.Add(fallback);
                RootPathTextBox.Text = fallback;
            }
            await ScanAsync();
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        await ScanAsync();
    }

    private async Task ScanAsync(bool throwOnError = false)
    {
        var selectedPath = selectedSite?.Path;
        if (Roots.Count == 0)
        {
            var path = RootPathTextBox.Text.Trim();
            if (path.Length > 0) Roots.Add(Path.GetFullPath(path));
        }
        if (Roots.Count == 0)
        {
            return;
        }

        ScanProgress.IsActive = true;
        Sites.Clear();
        VisibleSites.Clear();
        ShowSite(null);
        try
        {
            var tld = TldTextBox.Text.Trim();
            if (tld.Length == 0)
            {
                tld = "test";
                TldTextBox.Text = tld;
            }
            SaveSettings();
            var normalizedSettings = settingsStore.Load();
            tld = normalizedSettings.Tld;
            TldTextBox.Text = tld;
            var scanned = await coreClient.ScanAsync(
                Roots,
                tld,
                normalizedSettings.LinkedSites
            );
            foreach (var site in scanned)
            {
                Sites.Add(site);
            }
            ApplyFilter(selectedPath);
        }
        catch (Exception error)
        {
            if (throwOnError) throw;
            await ShowErrorAsync(error.Message);
        }
        finally
        {
            ScanProgress.IsActive = false;
            SiteCountText.Text = Sites.Count == 1 ? "1 site" : $"{Sites.Count} sites";
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter(selectedSite?.Path);
    }

    private void ApplyFilter(string? preferredPath)
    {
        var query = SearchBox.Text.Trim();
        VisibleSites.Clear();
        foreach (var site in SitePresentation.Filter(Sites, query))
        {
            VisibleSites.Add(site);
        }
        EmptyState.Visibility = VisibleSites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var selection = VisibleSites.FirstOrDefault(site => site.Path.Equals(
            preferredPath,
            StringComparison.OrdinalIgnoreCase
        )) ?? VisibleSites.FirstOrDefault();
        SiteList.SelectedItem = selection;
        ShowSite(selection);
    }

    private void SiteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowSite(SiteList.SelectedItem as SiteRecord);
    }

    private void ShowSite(SiteRecord? site)
    {
        selectedSite = site;
        NoSelectionState.Visibility = site is null ? Visibility.Visible : Visibility.Collapsed;
        SiteDetail.Visibility = site is null ? Visibility.Collapsed : Visibility.Visible;
        if (site is null) return;

        SiteNameText.Text = site.Name;
        DomainText.Text = site.Domain;
        FrameworkText.Text = site.Framework;
        RuntimeText.Text = SitePresentation.RuntimeLabel(site);
        RegistrationText.Text = site.Linked ? "Linked" : "Parked";
        PathText.Text = site.Path;
        UnlinkButton.IsEnabled = site.Linked;
        UrlButton.Content = SitePresentation.DisplayAddress(
            site,
            environment.IsRunning,
            environment.HttpPort,
            environment.HttpsPort
        );
        _ = RefreshPreviewAsync();
    }

    private Uri SiteUri(SiteRecord site)
    {
        return SitePresentation.SiteUri(
            site,
            environment.IsRunning,
            environment.HttpPort,
            environment.HttpsPort
        );
    }

    private async Task RefreshPreviewAsync()
    {
        PreviewBorder.Visibility = PreviewToggle.IsOn && selectedSite is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!PreviewToggle.IsOn || selectedSite is null) return;
        try
        {
            await SitePreview.EnsureCoreWebView2Async();
            await ApplyDesktopPreviewMetricsAsync();
            SitePreview.Source = SiteUri(selectedSite);
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
        }
    }

    private async Task ApplyDesktopPreviewMetricsAsync()
    {
        if (SitePreview.CoreWebView2 is null || SitePreview.ActualWidth <= 0) return;
        try
        {
            await SitePreview.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Emulation.setDeviceMetricsOverride",
                SitePresentation.DesktopPreviewMetricsJson(SitePreview.ActualWidth)
            );
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
        }
    }

    private async void SitePreview_NavigationCompleted(
        WebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args
    )
    {
        await ApplyDesktopPreviewMetricsAsync();
    }

    private async void SitePreview_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PreviewBorder.Visibility == Visibility.Visible)
        {
            await ApplyDesktopPreviewMetricsAsync();
        }
    }

    private void PreviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!suppressPreviewToggle)
        {
            SaveSettings(showPreviews: PreviewToggle.IsOn);
        }
        _ = RefreshPreviewAsync();
    }

    private async void OpenSite_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(SiteUri(selectedSite).AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{selectedSite.Path}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private async void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is null) return;
        try
        {
            var terminal = new ProcessStartInfo("wt.exe")
            {
                UseShellExecute = true,
                WorkingDirectory = selectedSite.Path
            };
            terminal.ArgumentList.Add("-d");
            terminal.ArgumentList.Add(selectedSite.Path);
            Process.Start(terminal);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = true,
                    WorkingDirectory = selectedSite.Path
                });
            }
            catch (Exception fallbackError) when (fallbackError is Win32Exception or InvalidOperationException)
            {
                await ShowErrorAsync(fallbackError.Message);
            }
        }
    }

    private void SaveSettings(bool? startAutomatically = null, bool? showPreviews = null)
    {
        settingsStore.UpdateSites(
            Roots,
            TldTextBox.Text,
            startAutomatically,
            showPreviews
        );
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "HerdMe",
            Content = message,
            CloseButtonText = "OK"
        };
        await dialog.ShowAsync();
    }
}
