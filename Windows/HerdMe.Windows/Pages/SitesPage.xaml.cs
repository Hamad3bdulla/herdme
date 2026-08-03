using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HerdMe.Windows.Pages;

public sealed partial class SitesPage : Page
{
    private sealed record DisplayOption(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record DatabaseServiceOption(ManagedServiceInstance Instance)
    {
        public override string ToString() => $"{Instance.Name} (127.0.0.1:{Instance.Port})";
    }

    private readonly CoreClient coreClient;
    private readonly WindowsLocalEnvironment environment;
    private readonly SiteConfigurationStore settingsStore;
    private readonly LaravelProjectCreator projectCreator;
    private CancellationTokenSource? projectCreationCancellation;
    private readonly SiteRuntimeStore siteRuntimeStore;
    private readonly SiteCommandFavoritesStore commandFavorites;
    private readonly PhpRuntimeInstaller phpInstaller;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly NodeRuntimeInstaller nodeInstaller;
    private readonly ComposerToolManager composerTools;
    private readonly WindowsServiceManager serviceManager;
    private readonly SiteProcessManager siteProcesses;
    private readonly WindowsCertificateManager certificates;
    private readonly SiteScanGeneration siteScanGeneration = new();
    private bool loaded;
    private bool suppressPreviewToggle = true;
    private SiteRecord? selectedSite;
    private CancellationTokenSource? artisanCancellation;
    private CancellationTokenSource? npmCancellation;
    private CancellationTokenSource? siteDetailsCancellation;
    private CancellationTokenSource? gitInspectionCancellation;
    private CancellationTokenSource? databaseCancellation;
    private CancellationTokenSource? siteOperationCancellation;

    public ObservableCollection<string> Roots { get; } = [];
    public ObservableCollection<SiteRecord> Sites { get; } = [];
    public ObservableCollection<SiteRecord> VisibleSites { get; } = [];

    public SitesPage(
        CoreClient coreClient,
        WindowsLocalEnvironment environment,
        SiteConfigurationStore settingsStore,
        LaravelProjectCreator projectCreator,
        SiteRuntimeStore siteRuntimeStore,
        SiteCommandFavoritesStore commandFavorites,
        PhpRuntimeInstaller phpInstaller,
        PhpRuntimePolicy runtimePolicy,
        NodeRuntimeInstaller nodeInstaller,
        ComposerToolManager composerTools,
        WindowsServiceManager serviceManager,
        SiteProcessManager siteProcesses,
        WindowsCertificateManager certificates
    )
    {
        this.coreClient = coreClient;
        this.environment = environment;
        this.settingsStore = settingsStore;
        this.projectCreator = projectCreator;
        this.siteRuntimeStore = siteRuntimeStore;
        this.commandFavorites = commandFavorites;
        this.phpInstaller = phpInstaller;
        this.runtimePolicy = runtimePolicy;
        this.nodeInstaller = nodeInstaller;
        this.composerTools = composerTools;
        this.serviceManager = serviceManager;
        this.siteProcesses = siteProcesses;
        this.certificates = certificates;
        InitializeComponent();
        var settings = settingsStore.Load();
        foreach (var root in settings.Roots) Roots.Add(root);
        Directory.CreateDirectory(Roots[0]);
        RootPathTextBox.Text = Roots[0];
        suppressPreviewToggle = true;
        PreviewToggle.IsOn = settings.ShowPreviews;
        suppressPreviewToggle = false;
        UpdateEnvironmentState();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        siteProcesses.Changed += SiteProcesses_Changed;
        await ScanAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        loaded = false;
        siteScanGeneration.Invalidate();
        CancelGitInspection();
        projectCreationCancellation?.Cancel();
        artisanCancellation?.Cancel();
        npmCancellation?.Cancel();
        siteDetailsCancellation?.Cancel();
        databaseCancellation?.Cancel();
        siteOperationCancellation?.Cancel();
        siteProcesses.Changed -= SiteProcesses_Changed;
    }

    private void UpdateEnvironmentState()
    {
        var running = environment.IsRunning;
        EnvironmentStatusText.Text = running
            ? AppLocalization.Get("SitesEnvironmentRunning")
            : environment.IsDegraded
                ? AppLocalization.Get("SitesEnvironmentRecovering")
                : AppLocalization.Get("SitesEnvironmentStopped");
        EnvironmentEndpointText.Text = running
            ? environment.HttpsPort is not null ? "HTTPS" : "HTTP"
            : string.Empty;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderPathAsync();
        if (path is not null) RootPathTextBox.Text = path;
    }

    private async void ParkFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderPathAsync();
        if (path is not null) await AddRootAsync(path);
    }

    private static async Task<string?> PickFolderPathAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
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
                AppLocalization.Get("SitesLinkOtherApplicationRejected")
            );
            return;
        }
        settingsStore.AddLinkedSite(folder.Path);
        await ScanAsync();
    }

    private async void UnlinkSelectedSite_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { Linked: true } site) return;
        await UnlinkSiteAsync(site);
    }

    private async Task UnlinkSiteAsync(SiteRecord site)
    {
        settingsStore.RemoveLinkedSite(site.Path);
        await ScanAsync();
    }

    private async void MoveSelectedSiteToRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { Linked: false } site) return;
        await MoveSiteToRecycleBinAsync(site);
    }

    private async void RemoveSiteFromMenu_Click(object sender, RoutedEventArgs e)
    {
        if (SiteFromMenu(sender) is not { } site) return;
        if (site.Linked)
        {
            await UnlinkSiteAsync(site);
        }
        else
        {
            await MoveSiteToRecycleBinAsync(site);
        }
    }

    private async Task MoveSiteToRecycleBinAsync(SiteRecord site)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesMoveToRecycleBinTitle", site.Name),
            Content = AppLocalization.Get("SitesMoveToRecycleBinMessage"),
            PrimaryButtonText = AppLocalization.Get("SitesMoveToRecycleBin"),
            CloseButtonText = AppLocalization.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await SiteRemovalService.MoveToRecycleBinAsync(site, settingsStore.Load().Roots);
            await ScanAsync(throwOnError: true);
            UpdateEnvironmentState();
        }
        catch (SiteRemovalException error)
        {
            var key = error.Failure switch
            {
                SiteRemovalFailure.LinkedProject => "SitesRemoveLinkedRejected",
                SiteRemovalFailure.OutsideParkedFolder => "SitesRemoveOutsideRootRejected",
                _ => "SitesRemoveUnavailable"
            };
            await ShowErrorAsync(AppLocalization.Get(key));
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or OperationCanceledException)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private async void CreateLaravel_Click(object sender, RoutedEventArgs e)
    {
        var parent = RootList.SelectedItem as string ?? Roots.FirstOrDefault();
        if (parent is null)
        {
            await ShowErrorAsync(AppLocalization.Get("SitesAddFolderBeforeCreate"));
            return;
        }
        var nameBox = new TextBox
        {
            Header = AppLocalization.Get("SitesProjectNameField"),
            PlaceholderText = "my-app"
        };
        var starterOptions = new[]
        {
            new DisplayOption("None", AppLocalization.Get("SitesStarterNone")),
            new DisplayOption("React", "React"),
            new DisplayOption("Vue", "Vue"),
            new DisplayOption("Svelte", "Svelte"),
            new DisplayOption("Livewire", "Livewire"),
            new DisplayOption("Custom", AppLocalization.Get("SitesStarterCustom"))
        };
        var starterBox = new ComboBox
        {
            Header = AppLocalization.Get("SitesStarterKitField"),
            ItemsSource = starterOptions,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var customStarterBox = new TextBox
        {
            Header = AppLocalization.Get("SitesCustomComposerPackageField"),
            PlaceholderText = "vendor/package",
            Visibility = Visibility.Collapsed
        };
        starterBox.SelectionChanged += (_, _) =>
        {
            customStarterBox.Visibility = (starterBox.SelectedItem as DisplayOption)?.Value == "Custom"
                ? Visibility.Visible
                : Visibility.Collapsed;
        };
        var testingOptions = new[]
        {
            new DisplayOption("Pest", "Pest"),
            new DisplayOption("PHPUnit", "PHPUnit")
        };
        var testingBox = new ComboBox
        {
            Header = AppLocalization.Get("SitesTestingFrameworkField"),
            ItemsSource = testingOptions,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var boostToggle = new ToggleSwitch
        {
            Header = AppLocalization.Get("SitesInstallBoostField"),
            IsOn = true
        };
        var gitToggle = new ToggleSwitch
        {
            Header = AppLocalization.Get("SitesInitializeGitField")
        };
        var content = new StackPanel { Spacing = 12, MinWidth = 380 };
        content.Children.Add(new TextBlock
        {
            Text = AppLocalization.Format("SitesProjectLocation", parent),
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
            Title = AppLocalization.Get("SitesCreateLaravelDialogTitle"),
            Content = content,
            PrimaryButtonText = AppLocalization.Get("SitesCreate"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var request = new LaravelProjectRequest(
            nameBox.Text,
            parent,
            (starterBox.SelectedItem as DisplayOption)?.Value ?? "None",
            (testingBox.SelectedItem as DisplayOption)?.Value ?? "Pest",
            boostToggle.IsOn,
            gitToggle.IsOn,
            (starterBox.SelectedItem as DisplayOption)?.Value == "Custom" ? customStarterBox.Text : null
        );
        var stages = LaravelProjectCreationStages.For(request);
        var statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = LocalizedStageDetail(stages[0])
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
            Title = AppLocalization.Get("SitesCreatingLaravelDialogTitle"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
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
        using var cancellation = new CancellationTokenSource();
        projectCreationCancellation = cancellation;
        var creationFinished = false;
        progressDialog.Closing += (_, args) =>
        {
            if (creationFinished) return;
            args.Cancel = true;
            if (cancellation.IsCancellationRequested) return;
            cancellation.Cancel();
            statusText.Text = AppLocalization.Get("SitesCancellingProjectCreation");
            progressDialog.CloseButtonText = string.Empty;
        };

        CreateLaravelButton.IsEnabled = false;
        ScanProgress.IsActive = true;
        var progressDialogOperation = progressDialog.ShowAsync();
        await Task.Yield();
        try
        {
            await projectCreator.CreateAsync(request, creationProgress, cancellation.Token);
            currentStage = LaravelProjectCreationStage.RegisteringSite;
            UpdateProjectCreationProgress(stages, stageRows, statusText, currentStage);
            await ScanAsync(throwOnError: true);
            currentStage = LaravelProjectCreationStage.Completed;
            UpdateProjectCreationProgress(stages, stageRows, statusText, currentStage);
            progressRing.IsActive = false;
            progressDialog.Title = AppLocalization.Get("SitesLaravelCreatedTitle");
            progressDialog.CloseButtonText = AppLocalization.Get("SitesDone");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            UpdateProjectCreationFailure(stageRows, currentStage);
            progressRing.IsActive = false;
            progressDialog.Title = AppLocalization.Get("SitesLaravelCreationCancelledTitle");
            statusText.Text = AppLocalization.Get("SitesLaravelCreationCancelledMessage");
            progressDialog.CloseButtonText = AppLocalization.Get("SitesClose");
        }
        catch (Exception error)
        {
            UpdateProjectCreationFailure(stageRows, currentStage);
            progressRing.IsActive = false;
            progressDialog.Title = AppLocalization.Get("SitesLaravelCreationFailedTitle");
            var failure = CommandErrorPresenter.Present(
                error.Message,
                AppLocalization.Get("SitesLaravelCreationFallback")
            );
            statusText.Text = failure.Message;
            if (failure.TechnicalDetails is not null)
            {
                progressContent.Children.Add(new Expander
                {
                    Header = AppLocalization.Get("SitesTechnicalDetails"),
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
            progressDialog.CloseButtonText = AppLocalization.Get("SitesClose");
        }
        finally
        {
            creationFinished = true;
            if (ReferenceEquals(projectCreationCancellation, cancellation))
            {
                projectCreationCancellation = null;
            }
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
            var prefix = AppLocalization.Get(
                index < currentIndex
                    ? "SitesProgressCompleted"
                    : index == currentIndex ? "SitesProgressInProgress" : "SitesProgressPending"
            );
            rows[stage].Text = AppLocalization.Format(
                "SitesProgressRow",
                prefix,
                LocalizedStageTitle(stage)
            );
            rows[stage].FontWeight = index == currentIndex
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
        }
        statusText.Text = LocalizedStageDetail(current);
    }

    private static void UpdateProjectCreationFailure(
        IReadOnlyDictionary<LaravelProjectCreationStage, TextBlock> rows,
        LaravelProjectCreationStage current
    )
    {
        rows[current].Text = AppLocalization.Format(
            "SitesProgressRow",
            AppLocalization.Get("SitesProgressFailed"),
            LocalizedStageTitle(current)
        );
        rows[current].FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
    }

    private static string LocalizedStageTitle(LaravelProjectCreationStage stage)
    {
        return AppLocalization.Get(LaravelProjectCreationStages.TitleKey(stage));
    }

    private static string LocalizedStageDetail(LaravelProjectCreationStage stage)
    {
        return AppLocalization.Get(LaravelProjectCreationStages.DetailKey(stage));
    }

    private async void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        await AddRootAsync(RootPathTextBox.Text);
    }

    private async Task AddRootAsync(string value)
    {
        var path = value.Trim();
        if (path.Length == 0) return;
        string normalized;
        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            await ShowErrorAsync(error.Message);
            return;
        }
        if (SiteConfigurationStore.BelongsToOtherHerd(normalized))
        {
            await ShowErrorAsync(
                AppLocalization.Get("SitesParkOtherApplicationRejected")
            );
            return;
        }
        if (!Directory.Exists(normalized))
        {
            await ShowErrorAsync(AppLocalization.Format("SitesParkFolderMissing", normalized));
            return;
        }
        RootPathTextBox.Text = normalized;
        if (!Roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            Roots.Add(normalized);
            SaveRoots();
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
        var phpOptions = new List<DisplayOption>
        {
            new(string.Empty, AppLocalization.Get("SitesRuntimeDefault"))
        };
        phpOptions.AddRange(phpInstaller.InstalledCycles().Select(version => new DisplayOption(version, version)));
        var nodeOptions = new List<DisplayOption>
        {
            new(string.Empty, AppLocalization.Get("SitesRuntimeDefault"))
        };
        nodeOptions.AddRange(nodeInstaller.InstalledVersions().Select(version => new DisplayOption(version, version)));
        var phpBox = new ComboBox
        {
            Header = "PHP",
            ItemsSource = phpOptions,
            SelectedItem = phpOptions.FirstOrDefault(option => option.Value == site.PhpVersion)
                ?? phpOptions[0],
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var nodeBox = new ComboBox
        {
            Header = "Node.js",
            ItemsSource = nodeOptions,
            SelectedItem = nodeOptions.FirstOrDefault(option => option.Value == site.NodeVersion)
                ?? nodeOptions[0],
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
            PrimaryButtonText = AppLocalization.Get("SitesSave"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var php = (phpBox.SelectedItem as DisplayOption)?.Value;
            var node = (nodeBox.SelectedItem as DisplayOption)?.Value;
            siteRuntimeStore.SetPhp(path, string.IsNullOrEmpty(php) ? null : php);
            siteRuntimeStore.SetNode(path, string.IsNullOrEmpty(node) ? null : node);
            await ScanAsync();
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
            SaveRoots();
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
        var generation = siteScanGeneration.Begin();
        CancelGitInspection();
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
            var normalizedSettings = settingsStore.Load();
            var scanned = await coreClient.ScanAsync(
                Roots,
                normalizedSettings.Tld,
                normalizedSettings.LinkedSites
            );
            if (!siteScanGeneration.IsCurrent(generation)) return;
            foreach (var site in scanned)
            {
                Sites.Add(site);
            }
            ApplyFilter(selectedPath);
            SiteCountText.Text = AppLocalization.Format("SitesCount", Sites.Count);
            if (scanned.Count > 0 && !environment.IsRunning)
            {
                EnvironmentStatusText.Text = AppLocalization.Get("SitesEnvironmentStarting");
            }
            await environment.SynchronizeSitesAsync(scanned);
            if (!siteScanGeneration.IsCurrent(generation)) return;
            UpdateEnvironmentState();
            await RefreshPreviewAsync();
            StartGitInspection(scanned, generation);
        }
        catch (Exception error)
        {
            if (!siteScanGeneration.IsCurrent(generation)) return;
            if (throwOnError) throw;
            await ShowErrorAsync(error.Message);
        }
        finally
        {
            if (siteScanGeneration.IsCurrent(generation))
            {
                ScanProgress.IsActive = false;
                SiteCountText.Text = AppLocalization.Format("SitesCount", Sites.Count);
                UpdateEnvironmentState();
            }
        }
    }

    private void StartGitInspection(IReadOnlyList<SiteRecord> sites, int generation)
    {
        var cancellation = new CancellationTokenSource();
        gitInspectionCancellation = cancellation;
        _ = InspectGitStatusesInBackgroundAsync(sites, generation, cancellation);
    }

    private async Task InspectGitStatusesInBackgroundAsync(
        IReadOnlyList<SiteRecord> sites,
        int generation,
        CancellationTokenSource cancellation
    )
    {
        try
        {
            var statuses = await SitePresentation.InspectGitStatusesAsync(
                sites,
                cancellation.Token
            );
            if (cancellation.IsCancellationRequested
                || !siteScanGeneration.IsCurrent(generation)) return;

            foreach (var site in Sites)
            {
                if (statuses.TryGetValue(site.Path, out var git))
                {
                    site.GitSummary = GitSummary(git);
                }
            }
            ApplyFilter(selectedSite?.Path);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!siteScanGeneration.IsCurrent(generation)) return;
            await DiagnosticLog.WriteFailureAsync(
                "site-git",
                "inspect",
                "Git status inspection failed after the site list was loaded.",
                error.ToString()
            );
        }
        finally
        {
            if (ReferenceEquals(gitInspectionCancellation, cancellation))
            {
                gitInspectionCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void CancelGitInspection()
    {
        var cancellation = gitInspectionCancellation;
        gitInspectionCancellation = null;
        cancellation?.Cancel();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter(selectedSite?.Path);
    }

    private void SearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Enter || selectedSite is null) return;
        e.Handled = true;
        OpenSite_Click(sender, e);
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
        var phpCycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
        var phpVersion = phpInstaller.InstalledVersion(phpCycle);
        var nodeVersion = site.NodeVersion ?? nodeInstaller.LoadSettings().ActiveVersion;
        RuntimeText.Text = AppLocalization.Format(
            "SitesRuntimeDetails",
            phpVersion is null ? phpCycle : $"{phpCycle} ({phpVersion})",
            string.IsNullOrWhiteSpace(nodeVersion)
                ? AppLocalization.Get("SitesProjectNodeRuntime")
                : nodeVersion
        );
        RegistrationText.Text = AppLocalization.Get(site.Linked ? "SitesLinked" : "SitesParked");
        PathText.Text = site.Path;
        UnlinkButton.Visibility = site.Linked ? Visibility.Visible : Visibility.Collapsed;
        RemoveSiteButton.Visibility = site.Linked ? Visibility.Collapsed : Visibility.Visible;
        ArtisanButton.IsEnabled = site.Framework == "Laravel"
            && File.Exists(Path.Combine(site.Path, "artisan"));
        NpmButton.IsEnabled = File.Exists(Path.Combine(site.Path, "package.json"));
        ComposerButton.IsEnabled = File.Exists(Path.Combine(site.Path, "composer.json"));
        if (TryCurrentSiteDatabase(site, out var databaseService, out var database, out _))
        {
            DatabaseDetailsText.Text = $"{databaseService.Name}: {database.DatabaseName} ({database.Username})";
        }
        else
        {
            DatabaseDetailsText.Text = AppLocalization.Get("SitesDatabaseNotConfigured");
        }
        HealthDetailsText.Text = AppLocalization.Get("SitesHealthCheckAvailable");
        UpdatePerformanceDetails(site);
        UrlButton.Content = SitePresentation.DisplayAddress(
            site,
            environment.IsRunning,
            environment.HttpPort,
            environment.HttpsPort
        );
        _ = RefreshSiteDetailsAsync(site);
        UpdateBackgroundProcessState();
        _ = RefreshPreviewAsync();
    }

    private void SiteProcesses_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateBackgroundProcessState);
    }

    private void UpdateBackgroundProcessState()
    {
        if (selectedSite is not { } site) return;
        var queue = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Queue);
        var scheduler = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Scheduler);
        QueueWorkerButton.Content = AppLocalization.Get("SitesManageQueueWorker");
        SchedulerButton.Content = AppLocalization.Get("SitesManageScheduler");
        BackgroundProcessesText.Text = AppLocalization.Format(
            "SitesBackgroundProcessStatus",
            queue.Running ? AppLocalization.Get("SitesRunning") : AppLocalization.Get("SitesStopped"),
            scheduler.Running ? AppLocalization.Get("SitesRunning") : AppLocalization.Get("SitesStopped")
        );
        ProcessesDetailsText.Text = BackgroundProcessesText.Text;
    }

    private void UpdatePerformanceDetails(SiteRecord site)
    {
        var snapshot = environment.Performance(site.Domain);
        PerformanceDetailsText.Text = AppLocalization.Format(
            "SitesPerformanceSummary",
            snapshot.RequestCount,
            snapshot.AverageDuration.TotalMilliseconds,
            snapshot.ServerErrorCount
        );
    }

    private async Task RefreshSiteDetailsAsync(SiteRecord site)
    {
        siteDetailsCancellation?.Cancel();
        siteDetailsCancellation?.Dispose();
        using var cancellation = new CancellationTokenSource();
        siteDetailsCancellation = cancellation;
        EnvironmentFileText.Text = AppLocalization.Get("SitesDetailsChecking");
        LogsText.Text = AppLocalization.Get("SitesDetailsChecking");
        RoutesText.Text = AppLocalization.Get("SitesDetailsChecking");
        GitText.Text = AppLocalization.Get("SitesDetailsChecking");
        AssociatedServicesText.Text = AppLocalization.Get("SitesDetailsChecking");
        try
        {
            var services = serviceManager.LoadInstances();
            var details = await Task.Run(
                () => InspectSiteDetails(site.Path, services, cancellation.Token),
                cancellation.Token
            );
            if (cancellation.IsCancellationRequested || !IsSelected(site)) return;
            EnvironmentFileText.Text = details.EnvironmentUnreadable
                ? AppLocalization.Get("SitesDetailsUnreadable")
                : AppLocalization.Get(
                    details.EnvironmentExists ? "SitesDetailsPresent" : "SitesDetailsMissing"
                );
            LogsText.Text = details.LogFileCount == 0
                ? AppLocalization.Get("SitesDetailsNoLogs")
                : details.LatestLogName is { } latest
                    ? AppLocalization.Format("SitesDetailsLogsLatest", details.LogFileCount, latest)
                    : AppLocalization.Format("SitesDetailsLogsCount", details.LogFileCount);
            RoutesText.Text = details.RouteFileNames.Count == 0
                ? AppLocalization.Get("SitesDetailsNoRoutes")
                : string.Join(", ", details.RouteFileNames);
            GitText.Text = !details.IsGitRepository
                ? AppLocalization.Get("SitesDetailsNotGit")
                : details.GitChangeCount == 0
                    ? AppLocalization.Format(
                        "SitesDetailsGitClean",
                        details.GitBranch ?? AppLocalization.Get("SitesDetailsDetached")
                    )
                    : AppLocalization.Format(
                        "SitesDetailsGitChanges",
                        details.GitBranch ?? AppLocalization.Get("SitesDetailsDetached"),
                        details.GitChangeCount
                    );
            AssociatedServicesText.Text = details.AssociatedServices.Count == 0
                ? AppLocalization.Get("SitesDetailsNoServices")
                : string.Join(", ", details.AssociatedServices);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or Win32Exception)
        {
            if (!cancellation.IsCancellationRequested && IsSelected(site))
            {
                GitText.Text = AppLocalization.Get("SitesDetailsUnavailable");
                await DiagnosticLog.WriteFailureAsync(
                    "site-details",
                    "inspect",
                    $"The details for {site.Name} could not be inspected.",
                    error.ToString()
                );
            }
        }
        finally
        {
            if (ReferenceEquals(siteDetailsCancellation, cancellation))
            {
                siteDetailsCancellation = null;
            }
        }
    }

    private sealed record SiteDetailsResult(
        bool EnvironmentExists,
        bool EnvironmentUnreadable,
        int LogFileCount,
        string? LatestLogName,
        IReadOnlyList<string> RouteFileNames,
        bool IsGitRepository,
        string? GitBranch,
        int GitChangeCount,
        IReadOnlyList<string> AssociatedServices
    );

    private static SiteDetailsResult InspectSiteDetails(
        string sitePath,
        IReadOnlyList<ManagedServiceInstance> services,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var environmentPath = Path.Combine(sitePath, ".env");
        var environmentExists = File.Exists(environmentPath);
        var environmentUnreadable = false;
        IReadOnlyDictionary<string, string> environmentValues = new Dictionary<string, string>();
        if (environmentExists)
        {
            try
            {
                var attributes = File.GetAttributes(environmentPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || new FileInfo(environmentPath).Length > 4 * 1_024 * 1_024)
                {
                    environmentUnreadable = true;
                }
                else
                {
                    environmentValues = ParseEnvironment(File.ReadAllText(environmentPath, new UTF8Encoding(false, true)));
                }
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or DecoderFallbackException)
            {
                environmentUnreadable = true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var logsPath = Path.Combine(sitePath, "storage", "logs");
        var logs = Directory.Exists(logsPath)
            ? Directory.EnumerateFiles(logsPath, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray()
            : [];
        var routesPath = Path.Combine(sitePath, "routes");
        var routes = Directory.Exists(routesPath)
            ? Directory.EnumerateFiles(routesPath, "*.php", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        cancellationToken.ThrowIfCancellationRequested();
        var git = SitePresentation.InspectGitAsync(sitePath, cancellationToken)
            .GetAwaiter()
            .GetResult();
        var associated = services.Where(service => MatchesService(service, environmentValues))
            .Select(service => $"{service.Name} ({service.Port})")
            .ToArray();
        return new SiteDetailsResult(
            environmentExists,
            environmentUnreadable,
            logs.Length,
            logs.FirstOrDefault()?.Name,
            routes,
            git.IsRepository,
            git.Branch,
            git.ChangeCount,
            associated
        );
    }

    private static IReadOnlyDictionary<string, string> ParseEnvironment(string contents)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in contents.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line.TrimStart();
            if (value.Length == 0 || value.StartsWith('#')) continue;
            var separator = value.IndexOf('=');
            if (separator <= 0) continue;
            var key = value[..separator].Trim();
            var item = value[(separator + 1)..].Trim();
            if (item.Length >= 2
                && (item[0] == '"' && item[^1] == '"' || item[0] == '\'' && item[^1] == '\''))
            {
                item = item[1..^1];
            }
            if (key.Length > 0) values[key] = item;
        }
        return values;
    }

    private static string? GitSummary(SiteGitStatus status)
    {
        if (!status.IsRepository) return null;
        var branch = status.Branch ?? AppLocalization.Get("SitesDetailsDetached");
        return status.ChangeCount == 0
            ? AppLocalization.Format("SitesDetailsGitClean", branch)
            : AppLocalization.Format("SitesDetailsGitChanges", branch, status.ChangeCount);
    }

    private static bool MatchesService(
        ManagedServiceInstance service,
        IReadOnlyDictionary<string, string> environment
    )
    {
        var port = service.Port.ToString();
        return service.DefinitionId switch
        {
            "mysql" or "mariadb" => Value("DB_PORT") == port && Value("DB_CONNECTION") == "mysql",
            "postgresql" => Value("DB_PORT") == port && Value("DB_CONNECTION") == "pgsql",
            "mongodb" => Value("MONGODB_URI")?.Contains($":{port}", StringComparison.Ordinal) == true,
            "redis" or "valkey" => Value("REDIS_PORT") == port,
            "meilisearch" => Value("MEILISEARCH_HOST")?.Contains($":{port}", StringComparison.Ordinal) == true,
            "typesense" => Value("TYPESENSE_PORT") == port,
            "minio" or "rustfs" => Value("AWS_ENDPOINT")?.Contains($":{port}", StringComparison.Ordinal) == true,
            _ => false
        };

        string? Value(string key) => environment.TryGetValue(key, out var value) ? value : null;
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
        var site = selectedSite;
        if (site is null)
        {
            PreviewBorder.Visibility = Visibility.Collapsed;
            return;
        }
        PreviewBorder.Visibility = PreviewToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!PreviewToggle.IsOn) return;
        PreviewFailureState.Visibility = Visibility.Collapsed;
        SitePreview.Visibility = Visibility.Visible;
        try
        {
            await SitePreview.EnsureCoreWebView2Async();
            await ApplyDesktopPreviewMetricsAsync();
            if (!IsSelected(site)) return;
            SitePreview.Source = SiteUri(site);
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
            await ReportPreviewFailureAsync("initialization", site, error.ToString());
            if (IsSelected(site)) ShowPreviewFailure();
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
            if (selectedSite is not null)
            {
                await ReportPreviewFailureAsync("desktop metrics", selectedSite, error.ToString());
            }
        }
    }

    private async void SitePreview_NavigationCompleted(
        WebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args
    )
    {
        if (!loaded || IsExpectedNavigationCancellation(args.WebErrorStatus)) return;
        if (!args.IsSuccess)
        {
            if (selectedSite is not null)
            {
                await ReportPreviewFailureAsync(
                    "navigation",
                    selectedSite,
                    $"WebView2 status: {args.WebErrorStatus}"
                );
                ShowPreviewFailure();
            }
            return;
        }
        PreviewFailureState.Visibility = Visibility.Collapsed;
        SitePreview.Visibility = Visibility.Visible;
        await ApplyDesktopPreviewMetricsAsync();
    }

    private static bool IsExpectedNavigationCancellation(CoreWebView2WebErrorStatus status)
    {
        return status is CoreWebView2WebErrorStatus.OperationCanceled
            or CoreWebView2WebErrorStatus.ConnectionAborted;
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
            settingsStore.UpdateShowPreviews(PreviewToggle.IsOn);
        }
        _ = RefreshPreviewAsync();
    }

    private void RetryPreview_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshPreviewAsync();
    }

    private bool IsSelected(SiteRecord site)
    {
        return selectedSite?.Path.Equals(site.Path, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void ShowPreviewFailure()
    {
        SitePreview.Visibility = Visibility.Collapsed;
        PreviewFailureState.Visibility = Visibility.Visible;
    }

    private async Task ReportPreviewFailureAsync(string stage, SiteRecord site, string details)
    {
        await DiagnosticLog.WriteFailureAsync(
            "site-preview",
            stage,
            $"The preview for {site.Name} at {site.Path} failed.",
            details
        );
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

    private void CopySelectedSiteLink_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not null) CopyText(SiteUri(selectedSite).AbsoluteUri);
    }

    private void CopySelectedSitePath_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not null) CopyText(selectedSite.Path);
    }

    private void CopySiteLinkFromMenu_Click(object sender, RoutedEventArgs e)
    {
        if (SiteFromMenu(sender) is { } site) CopyText(SiteUri(site).AbsoluteUri);
    }

    private void CopySitePathFromMenu_Click(object sender, RoutedEventArgs e)
    {
        if (SiteFromMenu(sender) is { } site) CopyText(site.Path);
    }

    private SiteRecord? SiteFromMenu(object sender)
    {
        if (sender is not FrameworkElement { Tag: string path }) return null;
        return Sites.FirstOrDefault(site =>
            site.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static void CopyText(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is null) return;
        try
        {
            var explorer = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true
            };
            explorer.ArgumentList.Add(selectedSite.Path);
            Process.Start(explorer);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private async void EditEnvironment_Click(object sender, RoutedEventArgs e)
    {
        var site = selectedSite;
        if (site is null) return;

        ProjectEnvironmentDocument document;
        try
        {
            document = await Task.Run(() => ProjectEnvironmentFile.Load(site.Path));
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            await ShowErrorAsync(error.Message);
            return;
        }
        if (!IsSelected(site)) return;

        var pathText = new TextBlock
        {
            Text = Path.Combine(site.Path, ".env"),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "TextFillColorSecondaryBrush"
            ],
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = true
        };
        var statusText = new TextBlock
        {
            Text = EnvironmentDocumentStatus(document),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "TextFillColorSecondaryBrush"
            ]
        };
        var editor = new TextBox
        {
            Header = AppLocalization.Get("SitesEnvironmentContentsField"),
            Text = document.Contents,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            IsSpellCheckEnabled = false,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            MinWidth = 640,
            Height = 420
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        var content = new StackPanel { Spacing = 10, MinWidth = 640 };
        content.Children.Add(pathText);
        content.Children.Add(statusText);
        content.Children.Add(editor);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesEnvironmentDialogTitle", site.Name),
            Content = content,
            PrimaryButtonText = AppLocalization.Get("SitesSave"),
            CloseButtonText = AppLocalization.Get("SitesClose"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };
        var isDirty = false;
        var discardArmed = false;
        editor.TextChanged += (_, _) =>
        {
            isDirty = !string.Equals(editor.Text, document.Contents, StringComparison.Ordinal);
            discardArmed = false;
            dialog.IsPrimaryButtonEnabled = isDirty;
            statusText.Text = isDirty
                ? AppLocalization.Get("SitesEnvironmentStatusUnsaved")
                : EnvironmentDocumentStatus(document);
            statusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "TextFillColorSecondaryBrush"
            ];
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (!isDirty) return;
            var deferral = args.GetDeferral();
            editor.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                var editedContents = editor.Text;
                var expectedRevision = document.Revision;
                document = await Task.Run(() => ProjectEnvironmentFile.Save(
                    site.Path,
                    editedContents,
                    expectedRevision
                ));
                isDirty = false;
                discardArmed = false;
                statusText.Text = AppLocalization.Get("SitesEnvironmentStatusSaved");
                statusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    "SystemFillColorSuccessBrush"
                ];
            }
            catch (ProjectEnvironmentChangedException)
            {
                statusText.Text = AppLocalization.Get("SitesEnvironmentExternalChange");
                statusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    "SystemFillColorCriticalBrush"
                ];
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException)
            {
                statusText.Text = error.Message;
                statusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    "SystemFillColorCriticalBrush"
                ];
            }
            finally
            {
                editor.IsEnabled = true;
                dialog.IsPrimaryButtonEnabled = isDirty;
                deferral.Complete();
            }
        };
        dialog.CloseButtonClick += (_, args) =>
        {
            if (!isDirty || discardArmed) return;
            args.Cancel = true;
            discardArmed = true;
            statusText.Text = AppLocalization.Get("SitesEnvironmentConfirmDiscard");
            statusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "SystemFillColorCautionBrush"
            ];
        };
        await dialog.ShowAsync();
    }

    private async void CreateDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        var services = serviceManager.LoadInstances()
            .Where(instance => SiteDatabaseProvisioner.SupportedDefinitions.Contains(
                instance.DefinitionId
            ))
            .Where(instance => serviceManager.State(instance.Id, instance.DefinitionId)
                == ManagedServiceState.Running)
            .Select(instance => new DatabaseServiceOption(instance))
            .ToArray();
        if (services.Length == 0)
        {
            await ShowErrorAsync(AppLocalization.Get("SitesDatabaseNoRunningService"));
            return;
        }

        var serviceBox = new ComboBox
        {
            Header = AppLocalization.Get("SitesDatabaseServiceField"),
            ItemsSource = services,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var nameBox = new TextBox
        {
            Header = AppLocalization.Get("SitesDatabaseNameField"),
            Text = SiteDatabaseProvisioner.SuggestedDatabaseName(site.Name),
            MaxLength = 63,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var validationText = new TextBlock
        {
            Text = AppLocalization.Get("SitesDatabaseNameValidation"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "SystemFillColorCriticalBrush"
            ]
        };
        var content = new StackPanel { Width = 420, Spacing = 10 };
        content.Children.Add(serviceBox);
        content.Children.Add(nameBox);
        content.Children.Add(validationText);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesDatabaseDialogTitle", site.Name),
            Content = content,
            PrimaryButtonText = AppLocalization.Get("SitesDatabaseCreate"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = true
        };
        nameBox.TextChanged += (_, _) =>
        {
            var valid = SiteDatabaseProvisioner.IsValidDatabaseName(nameBox.Text.Trim());
            dialog.IsPrimaryButtonEnabled = valid;
            validationText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || serviceBox.SelectedItem is not DatabaseServiceOption selectedService)
        {
            return;
        }

        databaseCancellation?.Cancel();
        databaseCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        databaseCancellation = cancellation;
        CreateDatabaseButton.IsEnabled = false;
        SiteDatabaseProvisioning provisioning;
        try
        {
            provisioning = await serviceManager.CreateSiteDatabaseAsync(
                selectedService.Instance,
                nameBox.Text.Trim(),
                cancellation.Token
            );
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException)
        {
            await ShowErrorAsync(error.Message);
            return;
        }
        finally
        {
            if (ReferenceEquals(databaseCancellation, cancellation)) databaseCancellation = null;
            cancellation.Dispose();
            CreateDatabaseButton.IsEnabled = true;
        }

        var usernameBox = new TextBox
        {
            Header = AppLocalization.Get("SitesDatabaseUsernameField"),
            Text = provisioning.Username,
            IsReadOnly = true
        };
        var passwordBox = new TextBox
        {
            Header = AppLocalization.Get("SitesDatabasePasswordField"),
            Text = provisioning.Password,
            IsReadOnly = true
        };
        var statusText = new TextBlock
        {
            Text = AppLocalization.Get("SitesDatabaseCreatedStatus"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "SystemFillColorSuccessBrush"
            ]
        };
        var resultContent = new StackPanel { Width = 420, Spacing = 10 };
        resultContent.Children.Add(new TextBlock
        {
            Text = $"{selectedService}  /  {provisioning.DatabaseName}",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        resultContent.Children.Add(usernameBox);
        resultContent.Children.Add(passwordBox);
        resultContent.Children.Add(new TextBlock
        {
            Text = Path.Combine(site.Path, ".env"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        });
        resultContent.Children.Add(statusText);
        var resultDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Get("SitesDatabaseCreatedTitle"),
            Content = resultContent,
            PrimaryButtonText = AppLocalization.Get("SitesDatabaseAddToEnvironment"),
            CloseButtonText = AppLocalization.Get("SitesDone"),
            DefaultButton = ContentDialogButton.Primary
        };
        var environmentUpdated = false;
        resultDialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var update = await Task.Run(() => serviceManager.AddSiteDatabaseToEnvironment(
                    site.Path,
                    selectedService.Instance,
                    provisioning
                ));
                environmentUpdated = true;
                statusText.Text = AppLocalization.Format(
                    "SitesDatabaseEnvironmentUpdated",
                    update.AddedKeys,
                    update.UpdatedKeys
                );
                statusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    "SystemFillColorSuccessBrush"
                ];
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or NotSupportedException)
            {
                args.Cancel = true;
                statusText.Text = error.Message;
                statusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    "SystemFillColorCriticalBrush"
                ];
            }
            finally
            {
                deferral.Complete();
            }
        };
        await resultDialog.ShowAsync();
        if (environmentUpdated && IsSelected(site)) await RefreshSiteDetailsAsync(site);
    }

    private async void ManageDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        if (!TryCurrentSiteDatabase(site, out var instance, out var provisioning, out var error))
        {
            await ShowErrorAsync(error);
            return;
        }
        var actions = new[]
        {
            new DisplayOption("inspect", AppLocalization.Get("SitesDatabaseInspect")),
            new DisplayOption("backup", AppLocalization.Get("SitesDatabaseBackup")),
            new DisplayOption("restore", AppLocalization.Get("SitesDatabaseRestore")),
            new DisplayOption("reset", AppLocalization.Get("SitesDatabaseResetPassword")),
            new DisplayOption("open", AppLocalization.Get("SitesDatabaseOpenTablePlus")),
            new DisplayOption("copy", AppLocalization.Get("SitesDatabaseCopySettings")),
            new DisplayOption("delete", AppLocalization.Get("SitesDatabaseDelete"))
        };
        var actionBox = new ComboBox
        {
            Header = AppLocalization.Get("SitesDatabaseActionField"),
            ItemsSource = actions,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var revealPassword = new CheckBox
        {
            Content = AppLocalization.Get("SitesDatabaseRevealPassword"),
            Visibility = Visibility.Collapsed
        };
        actionBox.SelectionChanged += (_, _) =>
        {
            revealPassword.Visibility = actionBox.SelectedItem is DisplayOption { Value: "copy" }
                ? Visibility.Visible
                : Visibility.Collapsed;
        };
        var content = new StackPanel { Width = 430, Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = $"{instance.Name} / {provisioning.DatabaseName}\n{provisioning.Username}@127.0.0.1:{instance.Port}",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(actionBox);
        content.Children.Add(revealPassword);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesDatabaseManageTitle", site.Name),
            Content = content,
            PrimaryButtonText = AppLocalization.Get("SitesContinue"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || actionBox.SelectedItem is not DisplayOption selectedAction) return;

        switch (selectedAction.Value)
        {
            case "inspect":
                await InspectDatabaseAsync(instance, provisioning);
                break;
            case "backup":
                await BackupDatabaseAsync(site, instance, provisioning);
                break;
            case "restore":
                await RestoreDatabaseAsync(site, instance, provisioning);
                break;
            case "reset":
                await ResetDatabasePasswordAsync(site, instance, provisioning);
                break;
            case "open":
                try
                {
                    TablePlusConnection.Open(
                        TablePlusConnection.UriForDatabase(instance, provisioning)
                            ?? throw new NotSupportedException(
                                AppLocalization.Get("SitesDatabaseTablePlusUnavailable")
                            )
                    );
                }
                catch (Exception openError) when (openError is IOException
                    or InvalidOperationException or NotSupportedException)
                {
                    await ShowErrorAsync(openError.Message);
                }
                break;
            case "copy":
                var password = revealPassword.IsChecked == true
                    ? provisioning.Password
                    : "********";
                CopyText(string.Join(Environment.NewLine,
                [
                    $"DB_CONNECTION={(instance.DefinitionId == "postgresql" ? "pgsql" : "mysql")}",
                    "DB_HOST=127.0.0.1",
                    $"DB_PORT={instance.Port}",
                    $"DB_DATABASE={provisioning.DatabaseName}",
                    $"DB_USERNAME={provisioning.Username}",
                    $"DB_PASSWORD={password}"
                ]));
                break;
            case "delete":
                await DeleteDatabaseAsync(site, instance, provisioning);
                break;
        }
    }

    private async Task InspectDatabaseAsync(
        ManagedServiceInstance instance,
        SiteDatabaseProvisioning provisioning
    )
    {
        DatabaseConnectionInspection inspection;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            inspection = await serviceManager.InspectSiteDatabaseAsync(
                instance, provisioning, cancellation.Token
            );
        }
        catch (Exception error) when (error is IOException or InvalidOperationException
            or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
        {
            await ShowErrorAsync(error is OperationCanceledException
                ? AppLocalization.Get("SitesDatabaseInspectionTimedOut")
                : error.Message);
            return;
        }
        var size = inspection.SizeBytes < 1_024
            ? $"{inspection.SizeBytes} B"
            : inspection.SizeBytes < 1_048_576
                ? $"{inspection.SizeBytes / 1_024d:F1} KB"
                : inspection.SizeBytes < 1_073_741_824
                    ? $"{inspection.SizeBytes / 1_048_576d:F1} MB"
                    : $"{inspection.SizeBytes / 1_073_741_824d:F1} GB";
        var text = inspection.Connected
            ? string.Join(Environment.NewLine,
            [
                AppLocalization.Get("SitesDatabaseInspectionSuccess"),
                $"{AppLocalization.Get("SitesDatabaseInspectionEngine")}: {instance.Name}",
                $"{AppLocalization.Get("SitesDatabaseInspectionVersion")}: {inspection.ServerVersion}",
                $"{AppLocalization.Get("SitesDatabaseInspectionTables")}: {inspection.TableCount}",
                $"{AppLocalization.Get("SitesDatabaseInspectionSize")}: {size}",
                $"{AppLocalization.Get("SitesDatabaseInspectionLatency")}: {inspection.ResponseTime.TotalMilliseconds:F0} ms",
                $"{AppLocalization.Get("SitesDatabaseInspectionEndpoint")}: 127.0.0.1:{instance.Port}"
            ])
            : $"{AppLocalization.Get("SitesDatabaseInspectionFailed")}\n{inspection.Message}";
        await ShowCommandResultAsync(
            AppLocalization.Get("SitesDatabaseInspectionTitle"), text, true
        );
    }

    private bool TryCurrentSiteDatabase(
        SiteRecord site,
        out ManagedServiceInstance instance,
        out SiteDatabaseProvisioning provisioning,
        out string error
    )
    {
        instance = null!;
        provisioning = null!;
        error = AppLocalization.Get("SitesDatabaseEnvironmentMissing");
        var path = Path.Combine(site.Path, ".env");
        if (!File.Exists(path)) return false;
        IReadOnlyDictionary<string, string> values;
        try
        {
            values = ParseEnvironment(File.ReadAllText(path, new UTF8Encoding(false, true)));
        }
        catch (Exception readError) when (readError is IOException
            or UnauthorizedAccessException or DecoderFallbackException)
        {
            error = readError.Message;
            return false;
        }
        if (!values.TryGetValue("DB_DATABASE", out var database)
            || !values.TryGetValue("DB_USERNAME", out var username)
            || !values.TryGetValue("DB_PASSWORD", out var password)
            || !values.TryGetValue("DB_PORT", out var portText)
            || !int.TryParse(portText, out var port)
            || !SiteDatabaseProvisioner.IsValidDatabaseName(database)) return false;
        instance = serviceManager.LoadInstances().FirstOrDefault(service =>
            service.Port == port && SiteDatabaseProvisioner.SupportedDefinitions.Contains(
                service.DefinitionId
            )
        )!;
        if (instance is null)
        {
            error = AppLocalization.Get("SitesDatabaseServiceMissing");
            return false;
        }
        provisioning = new SiteDatabaseProvisioning(database, username, password);
        return true;
    }

    private async Task BackupDatabaseAsync(
        SiteRecord site,
        ManagedServiceInstance instance,
        SiteDatabaseProvisioning provisioning
    )
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"{provisioning.DatabaseName}-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add("SQL", [".sql"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await RunSiteOperationAsync(
            AppLocalization.Get("SitesDatabaseBackingUp"),
            async (_, token) => await serviceManager.BackupSiteDatabaseAsync(
                instance,
                provisioning,
                file.Path,
                token
            )
        );
    }

    private async Task RestoreDatabaseAsync(
        SiteRecord site,
        ManagedServiceInstance instance,
        SiteDatabaseProvisioning provisioning
    )
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".sql");
        picker.FileTypeFilter.Add(".gz");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Get("SitesDatabaseRestoreConfirmTitle"),
            Content = AppLocalization.Format("SitesDatabaseRestoreConfirm", provisioning.DatabaseName),
            PrimaryButtonText = AppLocalization.Get("SitesDatabaseRestore"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        await RunSiteOperationAsync(
            AppLocalization.Get("SitesDatabaseRestoring"),
            async (_, token) => await serviceManager.RestoreSiteDatabaseAsync(
                instance,
                provisioning,
                file.Path,
                token
            )
        );
    }

    private async void ImportDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        if (!TryCurrentSiteDatabase(site, out var instance, out var provisioning, out var error))
        {
            await ShowErrorAsync(error);
            return;
        }
        await RestoreDatabaseAsync(site, instance, provisioning);
    }

    private async Task ResetDatabasePasswordAsync(
        SiteRecord site,
        ManagedServiceInstance instance,
        SiteDatabaseProvisioning provisioning
    )
    {
        var updated = provisioning with
        {
            Password = SiteDatabaseProvisioner.Generate(provisioning.DatabaseName).Password
        };
        var succeeded = await RunSiteOperationAsync(
            AppLocalization.Get("SitesDatabaseResettingPassword"),
            async (progress, token) =>
            {
                await serviceManager.ResetSiteDatabasePasswordAsync(
                    instance,
                    provisioning,
                    updated.Password,
                    token
                );
                try
                {
                    _ = ServiceEnvironmentFile.Update(
                        site.Path,
                        [new ServiceEnvironmentVariable("DB_PASSWORD", updated.Password)],
                        instance.Name
                    );
                }
                catch
                {
                    await serviceManager.ResetSiteDatabasePasswordAsync(
                        instance,
                        updated,
                        provisioning.Password,
                        CancellationToken.None
                    );
                    throw;
                }
            }
        );
        if (succeeded) CopyText(updated.Password);
    }

    private async Task DeleteDatabaseAsync(
        SiteRecord site,
        ManagedServiceInstance instance,
        SiteDatabaseProvisioning provisioning
    )
    {
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Get("SitesDatabaseDeleteConfirmTitle"),
            Content = AppLocalization.Format("SitesDatabaseDeleteConfirm", provisioning.DatabaseName),
            PrimaryButtonText = AppLocalization.Get("SitesDatabaseDelete"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        var succeeded = await RunSiteOperationAsync(
            AppLocalization.Get("SitesDatabaseDeleting"),
            async (progress, token) =>
            {
                await serviceManager.DeleteSiteDatabaseAsync(instance, provisioning, token);
                _ = ServiceEnvironmentFile.Update(
                    site.Path,
                    [
                        new ServiceEnvironmentVariable("DB_DATABASE", string.Empty),
                        new ServiceEnvironmentVariable("DB_USERNAME", string.Empty),
                        new ServiceEnvironmentVariable("DB_PASSWORD", string.Empty)
                    ],
                    instance.Name
                );
            }
        );
        if (succeeded)
        {
            await RefreshSiteDetailsAsync(site);
        }
    }

    private static string EnvironmentDocumentStatus(ProjectEnvironmentDocument document)
    {
        if (document.LoadedFromExample)
        {
            return AppLocalization.Get("SitesEnvironmentStatusFromExample");
        }
        return AppLocalization.Get(
            document.Exists ? "SitesEnvironmentStatusLoaded" : "SitesEnvironmentStatusMissing"
        );
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is null) return;
        App.MainWindow.NavigateToLogs(selectedSite.Path);
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

    private async void QueueWorker_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        await ShowQueueManagerAsync(site);
    }

    private async Task ShowQueueManagerAsync(SiteRecord site)
    {
        var stateText = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var connectionBox = new TextBox
        {
            Header = AppLocalization.Get("SitesQueueConnection"),
            PlaceholderText = AppLocalization.Get("SitesQueueDefault"),
            MaxLength = 128
        };
        var queueBox = new TextBox
        {
            Header = AppLocalization.Get("SitesQueueNames"),
            PlaceholderText = AppLocalization.Get("SitesQueueDefault"),
            MaxLength = 128
        };
        static NumberBox Number(string header, double value, double minimum, double maximum) => new()
        {
            Header = header, Value = value, Minimum = minimum, Maximum = maximum,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var triesBox = Number(AppLocalization.Get("SitesQueueTries"), 1, 1, 100);
        var timeoutBox = Number(AppLocalization.Get("SitesQueueTimeout"), 60, 0, 86_400);
        var sleepBox = Number(AppLocalization.Get("SitesQueueSleep"), 3, 0, 3_600);
        var maxJobsBox = Number(AppLocalization.Get("SitesQueueMaxJobs"), 0, 0, 1_000_000);
        var maxTimeBox = Number(AppLocalization.Get("SitesQueueMaxTime"), 0, 0, 2_592_000);
        var settingsGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        settingsGrid.Children.Add(connectionBox);
        Grid.SetColumn(queueBox, 1); settingsGrid.Children.Add(queueBox);
        Grid.SetRow(triesBox, 1); settingsGrid.Children.Add(triesBox);
        Grid.SetRow(timeoutBox, 1); Grid.SetColumn(timeoutBox, 1); settingsGrid.Children.Add(timeoutBox);
        Grid.SetRow(sleepBox, 2); settingsGrid.Children.Add(sleepBox);
        Grid.SetRow(maxJobsBox, 2); Grid.SetColumn(maxJobsBox, 1); settingsGrid.Children.Add(maxJobsBox);
        Grid.SetRow(maxTimeBox, 3); settingsGrid.Children.Add(maxTimeBox);
        var outputBox = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            Height = 210, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        var startStopButton = new Button();
        var failedButton = new Button { Content = AppLocalization.Get("SitesQueueFailedRefresh") };
        var retryButton = new Button { Content = AppLocalization.Get("SitesQueueRetryAll") };
        var flushButton = new Button { Content = AppLocalization.Get("SitesQueueFlushFailed") };
        var confirmFlush = new CheckBox { Content = AppLocalization.Get("SitesQueueFlushConfirm") };
        flushButton.IsEnabled = false;
        var restartButton = new Button { Content = AppLocalization.Get("SitesQueueRestart") };
        var jobIdBox = new TextBox
        {
            Header = AppLocalization.Get("SitesQueueFailedJobId"),
            PlaceholderText = AppLocalization.Get("SitesQueueFailedJobIdPlaceholder"),
            MaxLength = 255
        };
        var retryJobButton = new Button { Content = AppLocalization.Get("SitesQueueRetryJob"), IsEnabled = false };
        var forgetJobButton = new Button { Content = AppLocalization.Get("SitesQueueForgetJob"), IsEnabled = false };
        var progress = new ProgressRing { Width = 18, Height = 18 };
        var workerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var control in new Control[] { startStopButton, failedButton, progress })
        {
            workerButtons.Children.Add(control);
        }
        var maintenanceButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        foreach (var control in new Control[] { retryButton, restartButton, flushButton })
        {
            maintenanceButtons.Children.Add(control);
        }
        var content = new StackPanel { Spacing = 10, Width = 660 };
        content.Children.Add(stateText);
        content.Children.Add(settingsGrid);
        content.Children.Add(workerButtons);
        content.Children.Add(maintenanceButtons);
        var jobButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        jobButtons.Children.Add(retryJobButton);
        jobButtons.Children.Add(forgetJobButton);
        content.Children.Add(jobIdBox);
        content.Children.Add(jobButtons);
        content.Children.Add(confirmFlush);
        content.Children.Add(new TextBlock { Text = AppLocalization.Get("SitesQueueOutput"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(outputBox);
        using var cancellation = new CancellationTokenSource();
        var busy = false;
        var managementOutput = string.Empty;

        void RefreshState()
        {
            var state = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Queue);
            startStopButton.Content = AppLocalization.Get(state.Running ? "SitesStopQueueWorker" : "SitesStartQueueWorker");
            stateText.Text = state.Running
                ? AppLocalization.Format("SitesQueueRunningSince", state.StartedAt?.ToLocalTime().ToString("g") ?? string.Empty)
                : AppLocalization.Get("SitesQueueStopped");
            foreach (var control in new Control[] { connectionBox, queueBox, triesBox, timeoutBox, sleepBox, maxJobsBox, maxTimeBox }) control.IsEnabled = !state.Running && !busy;
            var output = string.IsNullOrWhiteSpace(managementOutput) ? state.Output : managementOutput;
            outputBox.Text = string.IsNullOrWhiteSpace(output) ? AppLocalization.Get("SitesNoCommandOutput") : output;
        }

        async Task RunActionAsync(string action, string? jobId = null)
        {
            if (busy) return;
            busy = true; progress.IsActive = true;
            foreach (var button in new[] { startStopButton, failedButton, retryButton, flushButton, restartButton, retryJobButton, forgetJobButton }) button.IsEnabled = false;
            try
            {
                var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
                await runtimePolicy.PrepareLaunchAsync(phpInstaller.PhpExecutable(cycle), cycle, cancellation.Token);
                var result = await QueueManagementService.RunAsync(
                    phpInstaller.PhpExecutable(cycle), site.Path, action, jobId,
                    composerTools.ManagedEnvironment(cycle), cancellation.Token
                );
                managementOutput = string.IsNullOrWhiteSpace(result.Output)
                    ? AppLocalization.Get("SitesQueueActionCompleted") : result.Output;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or InvalidDataException
                or InvalidOperationException or ArgumentException or TimeoutException)
            {
                managementOutput = error.Message;
            }
            finally
            {
                busy = false; progress.IsActive = false;
                foreach (var button in new[] { startStopButton, failedButton, retryButton, restartButton }) button.IsEnabled = true;
                flushButton.IsEnabled = confirmFlush.IsChecked == true;
                retryJobButton.IsEnabled = QueueManagementService.ValidJobId(jobIdBox.Text.Trim());
                forgetJobButton.IsEnabled = retryJobButton.IsEnabled && confirmFlush.IsChecked == true;
                RefreshState();
            }
        }

        startStopButton.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; startStopButton.IsEnabled = false;
            try
            {
                var state = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Queue);
                managementOutput = string.Empty;
                if (state.Running) await siteProcesses.StopAsync(site.Path, SiteBackgroundProcessKind.Queue);
                else
                {
                    var options = new SiteQueueWorkerOptions(
                        connectionBox.Text.Trim(), queueBox.Text.Trim(), (int)triesBox.Value,
                        (int)timeoutBox.Value, (int)sleepBox.Value, (int)maxJobsBox.Value,
                        (int)maxTimeBox.Value
                    );
                    _ = options.Arguments();
                    var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
                    await phpInstaller.EnsureManagedConfigurationAsync(cycle, cancellation.Token);
                    siteProcesses.Start(site.Path, SiteBackgroundProcessKind.Queue,
                        phpInstaller.PhpExecutable(cycle), composerTools.ManagedEnvironment(cycle), options);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                managementOutput = error.Message;
            }
            finally { busy = false; startStopButton.IsEnabled = true; RefreshState(); }
        };
        failedButton.Click += async (_, _) => await RunActionAsync("failed");
        retryButton.Click += async (_, _) => await RunActionAsync("retry-all");
        restartButton.Click += async (_, _) => await RunActionAsync("restart");
        jobIdBox.TextChanged += (_, _) =>
        {
            retryJobButton.IsEnabled = !busy && QueueManagementService.ValidJobId(jobIdBox.Text.Trim());
            forgetJobButton.IsEnabled = retryJobButton.IsEnabled && confirmFlush.IsChecked == true;
        };
        retryJobButton.Click += async (_, _) => await RunActionAsync("retry", jobIdBox.Text.Trim());
        forgetJobButton.Click += async (_, _) =>
        {
            if (confirmFlush.IsChecked == true) await RunActionAsync("forget", jobIdBox.Text.Trim());
        };
        confirmFlush.Checked += (_, _) =>
        {
            flushButton.IsEnabled = !busy;
            forgetJobButton.IsEnabled = !busy && QueueManagementService.ValidJobId(jobIdBox.Text.Trim());
        };
        confirmFlush.Unchecked += (_, _) =>
        {
            flushButton.IsEnabled = false;
            forgetJobButton.IsEnabled = false;
        };
        flushButton.Click += async (_, _) =>
        {
            if (confirmFlush.IsChecked != true) return;
            await RunActionAsync("flush");
            confirmFlush.IsChecked = false;
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshState();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = AppLocalization.Format("SitesQueueTitle", site.Name),
            Content = content, CloseButtonText = AppLocalization.Get("SitesClose")
        };
        dialog.Opened += async (_, _) => { RefreshState(); timer.Start(); await RunActionAsync("failed"); };
        dialog.Closing += (_, args) => { if (busy) args.Cancel = true; };
        await dialog.ShowAsync();
        timer.Stop(); cancellation.Cancel(); UpdateBackgroundProcessState();
    }

    private async void Scheduler_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        await ShowSchedulerManagerAsync(site);
    }

    private async Task ShowSchedulerManagerAsync(SiteRecord site)
    {
        var stateText = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var tasksBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 230,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        var outputBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 150,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        var progress = new ProgressRing { Width = 18, Height = 18 };
        var startStopButton = new Button();
        var refreshButton = new Button { Content = AppLocalization.Get("SitesSchedulerRefresh") };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        buttons.Children.Add(startStopButton);
        buttons.Children.Add(refreshButton);
        buttons.Children.Add(progress);
        var content = new StackPanel { Spacing = 10, Width = 620 };
        content.Children.Add(stateText);
        content.Children.Add(buttons);
        content.Children.Add(new TextBlock
        {
            Text = AppLocalization.Get("SitesSchedulerTasks"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(tasksBox);
        content.Children.Add(new TextBlock
        {
            Text = AppLocalization.Get("SitesSchedulerOutput"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(outputBox);
        using var cancellation = new CancellationTokenSource();
        var loading = false;
        var schedulerActionRunning = false;
        var loadTask = Task.CompletedTask;

        void RefreshProcessState()
        {
            var state = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Scheduler);
            startStopButton.Content = AppLocalization.Get(
                state.Running ? "SitesStopScheduler" : "SitesStartScheduler"
            );
            stateText.Text = state.Running
                ? AppLocalization.Format(
                    "SitesSchedulerRunningSince",
                    state.StartedAt?.ToLocalTime().ToString("g") ?? string.Empty
                )
                : AppLocalization.Get("SitesSchedulerStopped");
            outputBox.Text = string.IsNullOrWhiteSpace(state.Output)
                ? AppLocalization.Get("SitesNoCommandOutput")
                : state.Output;
        }

        async Task LoadTasksAsync()
        {
            if (loading) return;
            loading = true;
            refreshButton.IsEnabled = false;
            progress.IsActive = true;
            try
            {
                var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
                var php = phpInstaller.PhpExecutable(cycle);
                await runtimePolicy.PrepareLaunchAsync(php, cycle, cancellation.Token);
                var result = await ArtisanCommandRunner.RunAsync(
                    php,
                    site.Path,
                    ["schedule:list", "--no-ansi", "--no-interaction"],
                    composerTools.ManagedEnvironment(cycle),
                    TimeSpan.FromMinutes(2),
                    cancellationToken: cancellation.Token
                );
                tasksBox.Text = string.IsNullOrWhiteSpace(result.Output)
                    ? AppLocalization.Get("SitesNoCommandOutput")
                    : result.Output;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception error) when (error is IOException or InvalidDataException
                or InvalidOperationException or ArgumentException or TimeoutException)
            {
                tasksBox.Text = error.Message;
            }
            finally
            {
                loading = false;
                refreshButton.IsEnabled = true;
                progress.IsActive = false;
            }
        }

        startStopButton.Click += async (_, _) =>
        {
            schedulerActionRunning = true;
            startStopButton.IsEnabled = false;
            try
            {
                var state = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Scheduler);
                if (state.Running)
                {
                    await siteProcesses.StopAsync(site.Path, SiteBackgroundProcessKind.Scheduler);
                }
                else
                {
                    var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
                    await phpInstaller.EnsureManagedConfigurationAsync(cycle, cancellation.Token);
                    siteProcesses.Start(
                        site.Path,
                        SiteBackgroundProcessKind.Scheduler,
                        phpInstaller.PhpExecutable(cycle),
                        composerTools.ManagedEnvironment(cycle)
                    );
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException
                or InvalidOperationException or ArgumentException)
            {
                outputBox.Text = error.Message;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                schedulerActionRunning = false;
                startStopButton.IsEnabled = true;
                RefreshProcessState();
            }
        };
        refreshButton.Click += async (_, _) =>
        {
            loadTask = LoadTasksAsync();
            await loadTask;
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshProcessState();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesSchedulerTitle", site.Name),
            Content = content,
            CloseButtonText = AppLocalization.Get("SitesClose")
        };
        dialog.Closing += (_, args) =>
        {
            if (schedulerActionRunning) args.Cancel = true;
        };
        dialog.Opened += async (_, _) =>
        {
            RefreshProcessState();
            timer.Start();
            loadTask = LoadTasksAsync();
            await loadTask;
        };
        await dialog.ShowAsync();
        timer.Stop();
        cancellation.Cancel();
        await loadTask;
        UpdateBackgroundProcessState();
    }

    private async void BackgroundOutput_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        var queue = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Queue);
        var scheduler = siteProcesses.State(site.Path, SiteBackgroundProcessKind.Scheduler);
        var output = $"=== Queue ==={Environment.NewLine}{queue.Output}{Environment.NewLine}"
            + $"=== Scheduler ==={Environment.NewLine}{scheduler.Output}";
        await ShowCommandResultAsync(
            AppLocalization.Get("SitesBackgroundOutputTitle"),
            output,
            true
        );
    }

    private StackPanel CreateCommandFavoritesRow(
        SiteRecord site,
        string tool,
        Func<string?> currentCommand,
        Action<string> applyCommand
    )
    {
        var favoriteBox = new ComboBox
        {
            Header = AppLocalization.Get("SitesCommandFavorites"),
            PlaceholderText = AppLocalization.Get("SitesCommandFavoritesEmpty"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 320
        };
        var saveButton = new Button
        {
            Content = new SymbolIcon(Symbol.Favorite),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var deleteButton = new Button
        {
            Content = new SymbolIcon(Symbol.Delete),
            VerticalAlignment = VerticalAlignment.Bottom,
            IsEnabled = false
        };
        ToolTipService.SetToolTip(
            saveButton, AppLocalization.Get("SitesCommandFavoriteSaveTooltip")
        );
        ToolTipService.SetToolTip(
            deleteButton, AppLocalization.Get("SitesCommandFavoriteDeleteTooltip")
        );

        void Refresh(string? select = null)
        {
            var commands = commandFavorites.Load(site.Path, tool)
                .Select(item => item.Command)
                .ToArray();
            favoriteBox.ItemsSource = commands;
            favoriteBox.SelectedItem = select is null
                ? null
                : commands.FirstOrDefault(command => command.Equals(
                    select, StringComparison.OrdinalIgnoreCase
                ));
            deleteButton.IsEnabled = favoriteBox.SelectedItem is string;
        }

        favoriteBox.SelectionChanged += (_, _) =>
        {
            deleteButton.IsEnabled = favoriteBox.SelectedItem is string;
            if (favoriteBox.SelectedItem is string command) applyCommand(command);
        };
        saveButton.Click += async (_, _) =>
        {
            var command = currentCommand()?.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                await ShowErrorAsync(AppLocalization.Get("SitesCommandFavoriteMissing"));
                return;
            }
            try
            {
                commandFavorites.Add(site.Path, tool, command);
                Refresh(command);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException)
            {
                await ShowErrorAsync(error.Message);
            }
        };
        deleteButton.Click += async (_, _) =>
        {
            if (favoriteBox.SelectedItem is not string command) return;
            try
            {
                commandFavorites.Remove(site.Path, tool, command);
                Refresh();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or InvalidDataException or ArgumentException)
            {
                await ShowErrorAsync(error.Message);
            }
        };
        try
        {
            Refresh();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException)
        {
            favoriteBox.IsEnabled = false;
            saveButton.IsEnabled = false;
            deleteButton.IsEnabled = false;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(favoriteBox);
        row.Children.Add(saveButton);
        row.Children.Add(deleteButton);
        return row;
    }

    private async void Composer_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        var options = ComposerCommandRunner.Presets.Select(preset => new DisplayOption(
            preset.Id,
            AppLocalization.Get("SitesComposerPreset" + preset.Id.Replace("-", string.Empty))
        )).Append(new DisplayOption("require", AppLocalization.Get("SitesComposerPresetrequire")))
            .ToArray();
        var commandBox = new ComboBox
        {
            Header = AppLocalization.Get("SitesComposerCommandField"),
            ItemsSource = options,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var packageBox = new TextBox
        {
            Header = AppLocalization.Get("SitesComposerPackageField"),
            PlaceholderText = "vendor/package",
            Visibility = Visibility.Collapsed
        };
        commandBox.SelectionChanged += (_, _) =>
        {
            packageBox.Visibility = commandBox.SelectedItem is DisplayOption { Value: "require" }
                ? Visibility.Visible
                : Visibility.Collapsed;
        };
        var favorites = CreateCommandFavoritesRow(
            site,
            "composer",
            () => commandBox.SelectedItem is not DisplayOption selection
                ? null
                : selection.Value == "require"
                    ? string.IsNullOrWhiteSpace(packageBox.Text)
                        ? null
                        : $"require {packageBox.Text.Trim()}"
                    : selection.Value,
            command =>
            {
                var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var option = options.FirstOrDefault(item => item.Value == parts[0]);
                if (option is null) return;
                commandBox.SelectedItem = option;
                packageBox.Text = parts.Length == 2 ? parts[1] : string.Empty;
            }
        );
        var content = new StackPanel { Width = 430, Spacing = 10 };
        content.Children.Add(favorites);
        content.Children.Add(commandBox);
        content.Children.Add(packageBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesComposerDialogTitle", site.Name),
            Content = content,
            PrimaryButtonText = AppLocalization.Get("SitesRun"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || commandBox.SelectedItem is not DisplayOption selection) return;

        IReadOnlyList<string> arguments;
        try
        {
            arguments = selection.Value == "require"
                ? ComposerCommandRunner.RequireArguments(packageBox.Text)
                : ComposerCommandRunner.Presets.Single(item => item.Id == selection.Value).Arguments;
        }
        catch (ArgumentException error)
        {
            await ShowErrorAsync(error.Message);
            return;
        }
        await RunSiteOperationAsync(
            AppLocalization.Get("SitesComposerRunning"),
            async (progress, cancellationToken) =>
            {
                var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
                await phpInstaller.EnsureManagedConfigurationAsync(cycle, cancellationToken);
                var result = await ComposerCommandRunner.RunAsync(
                    phpInstaller.PhpExecutable(cycle),
                    composerTools.ComposerPath,
                    site.Path,
                    arguments,
                    composerTools.ManagedEnvironment(cycle),
                    progress,
                    cancellationToken
                );
                await ShowCommandResultAsync(
                    AppLocalization.Get("SitesComposerResultTitle"),
                    result.Output,
                    result.ExitCode == 0
                );
            }
        );
    }

    private async void Performance_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var recent = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 340,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        var resetButton = new Button { Content = AppLocalization.Get("SitesPerformanceReset") };
        var content = new StackPanel { Spacing = 12, Width = 650 };
        content.Children.Add(summary);
        content.Children.Add(resetButton);
        content.Children.Add(recent);

        void RefreshPerformance()
        {
            var snapshot = environment.Performance(site.Domain);
            var lastRequest = snapshot.LastRequestAt?.ToLocalTime().ToString("g")
                ?? AppLocalization.Get("SitesPerformanceNever");
            summary.Text = AppLocalization.Format(
                "SitesPerformanceReport",
                snapshot.RequestCount,
                snapshot.ActiveRequests,
                snapshot.ServerErrorCount,
                snapshot.AverageDuration.TotalMilliseconds,
                snapshot.SlowestDuration.TotalMilliseconds,
                lastRequest
            );
            recent.Text = snapshot.RecentRequests.Count == 0
                ? AppLocalization.Get("SitesPerformanceNoRequests")
                : string.Join(Environment.NewLine, snapshot.RecentRequests.Select(request =>
                    $"{request.Timestamp.ToLocalTime():HH:mm:ss}  {request.StatusCode}  "
                    + $"{request.Duration.TotalMilliseconds,8:0.0} ms  {request.Method} {request.Target}"
                ));
            UpdatePerformanceDetails(site);
        }

        resetButton.Click += (_, _) =>
        {
            environment.ResetPerformance(site.Domain);
            RefreshPerformance();
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshPerformance();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesPerformanceTitle", site.Name),
            Content = content,
            CloseButtonText = AppLocalization.Get("SitesClose")
        };
        dialog.Opened += (_, _) =>
        {
            RefreshPerformance();
            timer.Start();
        };
        await dialog.ShowAsync();
        timer.Stop();
    }

    private async void SiteDoctor_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
        var checks = await SiteHealthInspector.InspectAsync(
            site.Path,
            site.Domain,
            cycle,
            phpInstaller,
            composerTools,
            certificates
        );
        var report = string.Join(Environment.NewLine, checks.Select(check =>
            $"{(check.Healthy ? "[OK]" : "[!] ")} {check.Name}: {check.Detail}"
        ));
        HealthDetailsText.Text = AppLocalization.Format(
            "SitesHealthSummary",
            checks.Count(check => check.Healthy),
            checks.Count
        );
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesDoctorTitle", site.Name),
            Content = new TextBox
            {
                Text = report,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 300
            },
            PrimaryButtonText = AppLocalization.Get("SitesRepair"),
            CloseButtonText = AppLocalization.Get("SitesDone")
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunSiteOperationAsync(
            AppLocalization.Get("SitesRepairing"),
            async (progress, cancellationToken) =>
            {
                progress.Report(AppLocalization.Get("SitesRepairEnvironment") + Environment.NewLine);
                var document = await Task.Run(() => ProjectEnvironmentFile.Load(site.Path), cancellationToken);
                if (!document.Exists)
                {
                    document = await Task.Run(() => ProjectEnvironmentFile.Save(
                        site.Path,
                        document.Contents,
                        document.Revision
                    ), cancellationToken);
                }
                if (SiteHealthInspector.EnvironmentValue(document.Contents, "APP_KEY") is null
                    && File.Exists(Path.Combine(site.Path, "artisan")))
                {
                    _ = ServiceEnvironmentFile.Update(
                        site.Path,
                        [new ServiceEnvironmentVariable("APP_KEY", string.Empty)],
                        "Laravel application key"
                    );
                    document = await Task.Run(
                        () => ProjectEnvironmentFile.Load(site.Path),
                        cancellationToken
                    );
                }

                progress.Report(AppLocalization.Get("SitesRepairRuntime") + Environment.NewLine);
                await phpInstaller.EnsureManagedConfigurationAsync(cycle, cancellationToken);
                if (!File.Exists(composerTools.ComposerPath))
                {
                    await composerTools.InstallOrUpdateAsync(cycle, cancellationToken);
                }
                var php = phpInstaller.PhpExecutable(cycle);
                var managedEnvironment = composerTools.ManagedEnvironment(cycle);
                if (File.Exists(Path.Combine(site.Path, "composer.json"))
                    && !File.Exists(Path.Combine(site.Path, "vendor", "autoload.php")))
                {
                    progress.Report(AppLocalization.Get("SitesRepairDependencies") + Environment.NewLine);
                    var composerResult = await ComposerCommandRunner.RunAsync(
                        php,
                        composerTools.ComposerPath,
                        site.Path,
                        ["install", "--no-interaction"],
                        managedEnvironment,
                        progress,
                        cancellationToken
                    );
                    if (composerResult.ExitCode != 0)
                    {
                        throw new InvalidOperationException(composerResult.Output);
                    }
                }

                if (File.Exists(Path.Combine(site.Path, "artisan")))
                {
                    progress.Report(AppLocalization.Get("SitesRepairLaravel") + Environment.NewLine);
                    foreach (var directory in LaravelWritableDirectories(site.Path))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    if (string.IsNullOrWhiteSpace(
                        SiteHealthInspector.EnvironmentValue(document.Contents, "APP_KEY")
                    ))
                    {
                        await RunRepairArtisanAsync(
                            site,
                            php,
                            managedEnvironment,
                            ["key:generate", "--force", "--no-interaction"],
                            progress,
                            cancellationToken
                        );
                    }
                    if (!Directory.Exists(Path.Combine(site.Path, "public", "storage")))
                    {
                        await RunRepairArtisanAsync(
                            site,
                            php,
                            managedEnvironment,
                            ["storage:link", "--no-interaction"],
                            progress,
                            cancellationToken
                        );
                    }
                    await RunRepairArtisanAsync(
                        site,
                        php,
                        managedEnvironment,
                        ["optimize:clear", "--no-interaction"],
                        progress,
                        cancellationToken
                    );
                }

                progress.Report(AppLocalization.Get("SitesRepairLocalServer") + Environment.NewLine);
                if (!certificates.IsAuthorityTrusted()) certificates.TrustAuthority();
                await environment.StartConfiguredAsync(settingsStore, cancellationToken);
                var repairedChecks = await SiteHealthInspector.InspectAsync(
                    site.Path,
                    site.Domain,
                    cycle,
                    phpInstaller,
                    composerTools,
                    certificates,
                    cancellationToken
                );
                HealthDetailsText.Text = AppLocalization.Format(
                    "SitesHealthSummary",
                    repairedChecks.Count(check => check.Healthy),
                    repairedChecks.Count
                );
                await RefreshSiteDetailsAsync(site);
            }
        );
    }

    private static IReadOnlyList<string> LaravelWritableDirectories(string sitePath) =>
    [
        Path.Combine(sitePath, "storage", "framework", "cache"),
        Path.Combine(sitePath, "storage", "framework", "sessions"),
        Path.Combine(sitePath, "storage", "framework", "views"),
        Path.Combine(sitePath, "storage", "logs"),
        Path.Combine(sitePath, "bootstrap", "cache")
    ];

    private static async Task RunRepairArtisanAsync(
        SiteRecord site,
        string php,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> arguments,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        var result = await ArtisanCommandRunner.RunAsync(
            php,
            site.Path,
            arguments,
            environment,
            TimeSpan.FromMinutes(10),
            progress,
            cancellationToken
        );
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Output);
    }

    private async void PhpExtensions_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
        await RunSiteOperationAsync(
            AppLocalization.Get("SitesPhpExtensionsRepairing"),
            async (_, cancellationToken) =>
            {
                await phpInstaller.EnsureManagedConfigurationAsync(cycle, cancellationToken);
                var report = await coreClient.ValidatePhpAsync(
                    phpInstaller.PhpExecutable(cycle),
                    cancellationToken
                );
                await ShowCommandResultAsync(
                    AppLocalization.Format("SitesPhpExtensionsTitle", cycle),
                    string.Join(Environment.NewLine, report.Loaded.Order(StringComparer.OrdinalIgnoreCase)),
                    report.Compatible
                );
            }
        );
    }

    private void CancelSiteOperation_Click(object sender, RoutedEventArgs e)
    {
        siteOperationCancellation?.Cancel();
    }

    private async Task<bool> RunSiteOperationAsync(
        string title,
        Func<IProgress<string>, CancellationToken, Task> operation
    )
    {
        siteOperationCancellation?.Cancel();
        siteOperationCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        siteOperationCancellation = cancellation;
        SiteOperationBar.Title = title;
        SiteOperationBar.Message = string.Empty;
        SiteOperationBar.Severity = InfoBarSeverity.Informational;
        SiteOperationBar.IsOpen = true;
        SiteOperationProgress.Visibility = Visibility.Visible;
        var progress = new Progress<string>(text =>
        {
            var value = (SiteOperationBar.Message + text).Trim();
            SiteOperationBar.Message = value.Length > 2_000 ? value[^2_000..] : value;
        });
        try
        {
            await operation(progress, cancellation.Token);
            SiteOperationBar.Severity = InfoBarSeverity.Success;
            SiteOperationBar.Title = AppLocalization.Get("SitesOperationCompleted");
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SiteOperationBar.Severity = InfoBarSeverity.Warning;
            SiteOperationBar.Title = AppLocalization.Get("SitesOperationCancelled");
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            SiteOperationBar.Severity = InfoBarSeverity.Error;
            SiteOperationBar.Title = AppLocalization.Get("SitesOperationFailed");
            SiteOperationBar.Message = error.Message;
            return false;
        }
        finally
        {
            if (ReferenceEquals(siteOperationCancellation, cancellation))
            {
                siteOperationCancellation = null;
            }
            cancellation.Dispose();
            SiteOperationProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task ShowCommandResultAsync(string title, string output, bool success)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(output)
                    ? AppLocalization.Get("SitesNoCommandOutput")
                    : output,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                Height = 320
            },
            CloseButtonText = AppLocalization.Get("SitesDone")
        };
        await dialog.ShowAsync();
    }

    private async void Artisan_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;

        var presetOptions = ArtisanCommandCatalog.Presets.Select(preset => new DisplayOption(
            preset.Id,
            AppLocalization.Get(ArtisanPresetTitleKey(preset.Id))
        )).ToArray();
        var presetBox = new ComboBox
        {
            Header = AppLocalization.Get("SitesArtisanCommandField"),
            ItemsSource = presetOptions,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        IReadOnlyList<string> artisanSuggestions = ArtisanCommandCatalog.Suggestions;
        var customBox = new AutoSuggestBox
        {
            Header = AppLocalization.Get("SitesArtisanCustomCommandField"),
            PlaceholderText = "route:list --path=api",
            ItemsSource = artisanSuggestions,
            UpdateTextOnSelect = true,
            Visibility = Visibility.Collapsed
        };
        customBox.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var query = customBox.Text.Trim();
            customBox.ItemsSource = artisanSuggestions
                .Where(command => query.Length == 0
                    || command.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        };
        customBox.SuggestionChosen += (_, args) =>
        {
            if (args.SelectedItem is string command) customBox.Text = command;
        };
        presetBox.SelectionChanged += (_, _) =>
        {
            customBox.Visibility = (presetBox.SelectedItem as DisplayOption)?.Value == "custom"
                ? Visibility.Visible
                : Visibility.Collapsed;
        };
        var favorites = CreateCommandFavoritesRow(
            site,
            "artisan",
            () => presetBox.SelectedItem is not DisplayOption selection
                ? null
                : selection.Value == "custom"
                    ? customBox.Text.Trim()
                    : string.Join(' ', ArtisanCommandCatalog.Presets
                        .Single(item => item.Id == selection.Value).Arguments),
            command =>
            {
                presetBox.SelectedItem = presetOptions.Single(item => item.Value == "custom");
                customBox.Text = command;
            }
        );
        var statusText = new TextBlock
        {
            Text = AppLocalization.Get("SitesArtisanReady"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        var progressRing = new ProgressRing { Width = 18, Height = 18 };
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        statusRow.Children.Add(progressRing);
        statusRow.Children.Add(statusText);
        var outputBox = new TextBox
        {
            Text = AppLocalization.Get("SitesArtisanOutputPlaceholder"),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 240,
            VerticalAlignment = VerticalAlignment.Top,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        ScrollViewer.SetVerticalScrollBarVisibility(outputBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(outputBox, ScrollBarVisibility.Auto);
        var runButton = new Button
        {
            Content = AppLocalization.Get("SitesRun"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        var cancelButton = new Button
        {
            Content = AppLocalization.Get("SitesCancel"),
            IsEnabled = false
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(runButton);
        var content = new StackPanel { Spacing = 12, Width = 500 };
        content.Children.Add(favorites);
        content.Children.Add(presetBox);
        content.Children.Add(customBox);
        content.Children.Add(buttons);
        content.Children.Add(statusRow);
        content.Children.Add(outputBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesArtisanDialogTitle", site.Name),
            Content = content,
            CloseButtonText = AppLocalization.Get("SitesClose")
        };
        using var suggestionCancellation = new CancellationTokenSource();
        dialog.Opened += async (_, _) =>
        {
            try
            {
                var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
                var php = phpInstaller.PhpExecutable(cycle);
                await runtimePolicy.PrepareLaunchAsync(
                    php,
                    cycle,
                    suggestionCancellation.Token
                );
                artisanSuggestions = await ArtisanCommandRunner.DiscoverCommandsAsync(
                    php,
                    site.Path,
                    composerTools.ManagedEnvironment(cycle),
                    suggestionCancellation.Token
                );
                customBox.ItemsSource = artisanSuggestions;
            }
            catch (OperationCanceledException) when (suggestionCancellation.IsCancellationRequested)
            {
                // Closing the dialog cancels command discovery without changing the fallback list.
            }
            catch (Exception error)
            {
                await DiagnosticLog.WriteFailureAsync(
                    "artisan",
                    "suggestions",
                    $"Artisan command discovery for {site.Name} failed.",
                    error.ToString()
                );
            }
        };

        var running = false;
        cancelButton.Click += (_, _) =>
        {
            if (!running) return;
            statusText.Text = AppLocalization.Get("SitesArtisanCancelling");
            artisanCancellation?.Cancel();
        };
        dialog.Closing += (_, args) =>
        {
            suggestionCancellation.Cancel();
            if (!running) return;
            args.Cancel = true;
            statusText.Text = AppLocalization.Get("SitesArtisanCancelling");
            artisanCancellation?.Cancel();
        };
        runButton.Click += async (_, _) =>
        {
            if (running) return;
            if (presetBox.SelectedItem is not DisplayOption selectedPreset) return;
            ArtisanCommandSpec command;
            try
            {
                command = ArtisanCommandCatalog.Resolve(
                    selectedPreset.Value,
                    customBox.Text
                );
            }
            catch (ArgumentException error)
            {
                statusText.Text = AppLocalization.Get("SitesArtisanFailed");
                outputBox.Text = error.Message;
                return;
            }

            using var cancellation = new CancellationTokenSource();
            artisanCancellation = cancellation;
            running = true;
            runButton.IsEnabled = false;
            cancelButton.IsEnabled = true;
            presetBox.IsEnabled = false;
            customBox.IsEnabled = false;
            progressRing.IsActive = true;
            statusText.Text = AppLocalization.Get("SitesArtisanValidatingPhp");
            outputBox.Text = string.Empty;
            try
            {
                var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
                var php = phpInstaller.PhpExecutable(cycle);
                await runtimePolicy.PrepareLaunchAsync(php, cycle, cancellation.Token);
                var environmentVariables = composerTools.ManagedEnvironment(cycle);
                statusText.Text = AppLocalization.Get("SitesArtisanRunning");
                var result = await ArtisanCommandRunner.RunAsync(
                    php,
                    site.Path,
                    command.Arguments,
                    environmentVariables,
                    command.Timeout,
                    new Progress<string>(chunk => AppendCommandOutput(outputBox, chunk)),
                    cancellation.Token
                );
                statusText.Text = result.ExitCode == 0
                    ? AppLocalization.Get("SitesArtisanCompleted")
                    : AppLocalization.Format("SitesArtisanFailedExit", result.ExitCode);
                if (outputBox.Text.Length == 0) outputBox.Text = result.Output;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                statusText.Text = AppLocalization.Get("SitesArtisanCancelled");
            }
            catch (Exception error)
            {
                statusText.Text = AppLocalization.Get(
                    error is TimeoutException ? "SitesArtisanTimedOut" : "SitesArtisanFailed"
                );
                AppendCommandOutput(outputBox, error.Message);
                await DiagnosticLog.WriteFailureAsync(
                    "artisan",
                    "run",
                    $"The Artisan command for {site.Name} failed.",
                    error.ToString()
                );
            }
            finally
            {
                running = false;
                progressRing.IsActive = false;
                runButton.IsEnabled = true;
                cancelButton.IsEnabled = false;
                presetBox.IsEnabled = true;
                customBox.IsEnabled = true;
                if (ReferenceEquals(artisanCancellation, cancellation)) artisanCancellation = null;
            }
        };

        await dialog.ShowAsync();
    }

    private async void Npm_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;

        IReadOnlyList<NpmScript> discoveredScripts;
        try
        {
            discoveredScripts = await Task.Run(() => NpmScriptCatalog.Discover(site.Path));
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NpmScriptException)
        {
            await ShowErrorAsync(NpmErrorMessage(error));
            return;
        }

        IReadOnlyList<string> scriptSuggestions = discoveredScripts
            .Select(script => script.Name)
            .ToArray();
        var scriptBox = new AutoSuggestBox
        {
            Header = AppLocalization.Get("SitesNpmScriptField"),
            ItemsSource = scriptSuggestions,
            Text = scriptSuggestions[0],
            UpdateTextOnSelect = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 440
        };
        scriptBox.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var query = scriptBox.Text.Trim();
            scriptBox.ItemsSource = scriptSuggestions
                .Where(script => query.Length == 0
                    || script.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        };
        scriptBox.SuggestionChosen += (_, args) =>
        {
            if (args.SelectedItem is string script) scriptBox.Text = script;
        };
        var favorites = CreateCommandFavoritesRow(
            site,
            "npm",
            () => scriptBox.Text.Trim(),
            command => scriptBox.Text = command
        );
        var reloadButton = new Button
        {
            Content = new SymbolIcon(Symbol.Refresh),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        ToolTipService.SetToolTip(
            reloadButton,
            AppLocalization.Get("SitesNpmReloadTooltip")
        );
        var scriptRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        scriptRow.Children.Add(scriptBox);
        scriptRow.Children.Add(reloadButton);

        var statusText = new TextBlock
        {
            Text = AppLocalization.Get("SitesNpmReady"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        var progressRing = new ProgressRing { Width = 18, Height = 18 };
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        statusRow.Children.Add(progressRing);
        statusRow.Children.Add(statusText);
        var outputBox = new TextBox
        {
            Text = AppLocalization.Get("SitesNpmOutputPlaceholder"),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 240,
            VerticalAlignment = VerticalAlignment.Top,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        ScrollViewer.SetVerticalScrollBarVisibility(outputBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(outputBox, ScrollBarVisibility.Auto);
        var runButton = new Button
        {
            Content = AppLocalization.Get("SitesRun"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        var cancelButton = new Button
        {
            Content = AppLocalization.Get("SitesCancel"),
            IsEnabled = false
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(runButton);
        var content = new StackPanel { Spacing = 12, Width = 500 };
        content.Children.Add(favorites);
        content.Children.Add(scriptRow);
        content.Children.Add(buttons);
        content.Children.Add(statusRow);
        content.Children.Add(outputBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesNpmDialogTitle", site.Name),
            Content = content,
            CloseButtonText = AppLocalization.Get("SitesClose")
        };

        var running = false;
        reloadButton.Click += async (_, _) =>
        {
            if (running) return;
            reloadButton.IsEnabled = false;
            scriptBox.IsEnabled = false;
            progressRing.IsActive = true;
            statusText.Text = AppLocalization.Get("SitesNpmLoading");
            try
            {
                var reloaded = await Task.Run(() => NpmScriptCatalog.Discover(site.Path));
                scriptSuggestions = reloaded.Select(script => script.Name).ToArray();
                scriptBox.ItemsSource = scriptSuggestions;
                scriptBox.Text = scriptSuggestions[0];
                statusText.Text = AppLocalization.Get("SitesNpmReady");
                outputBox.Text = string.Empty;
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or ArgumentException
                or NpmScriptException)
            {
                scriptBox.ItemsSource = Array.Empty<DisplayOption>();
                statusText.Text = AppLocalization.Get("SitesNpmUnavailable");
                outputBox.Text = NpmErrorMessage(error);
            }
            finally
            {
                progressRing.IsActive = false;
                reloadButton.IsEnabled = true;
                scriptBox.IsEnabled = true;
            }
        };
        cancelButton.Click += (_, _) =>
        {
            if (!running) return;
            statusText.Text = AppLocalization.Get("SitesNpmCancelling");
            npmCancellation?.Cancel();
        };
        dialog.Closing += (_, args) =>
        {
            if (!running) return;
            args.Cancel = true;
            statusText.Text = AppLocalization.Get("SitesNpmCancelling");
            npmCancellation?.Cancel();
        };
        runButton.Click += async (_, _) =>
        {
            if (running) return;
            var selectedScript = scriptBox.Text.Trim();
            if (selectedScript.Length == 0) return;

            using var cancellation = new CancellationTokenSource();
            npmCancellation = cancellation;
            running = true;
            runButton.IsEnabled = false;
            cancelButton.IsEnabled = true;
            reloadButton.IsEnabled = false;
            scriptBox.IsEnabled = false;
            progressRing.IsActive = true;
            statusText.Text = AppLocalization.Get("SitesNpmPreparingNode");
            outputBox.Text = string.Empty;
            try
            {
                var invocation = NpmScriptRunner.CreateInvocation(
                    nodeInstaller,
                    site.Path,
                    site.NodeVersion,
                    selectedScript
                );
                statusText.Text = AppLocalization.Get("SitesNpmRunning");
                var result = await NpmScriptRunner.RunAsync(
                    invocation,
                    new Progress<string>(chunk => AppendCommandOutput(outputBox, chunk)),
                    cancellation.Token
                );
                statusText.Text = result.ExitCode == 0
                    ? AppLocalization.Get("SitesNpmCompleted")
                    : AppLocalization.Format("SitesNpmFailedExit", result.ExitCode);
                if (outputBox.Text.Length == 0) outputBox.Text = result.Output;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                statusText.Text = AppLocalization.Get("SitesNpmCancelled");
            }
            catch (Exception error)
            {
                statusText.Text = AppLocalization.Get(
                    error is NpmScriptException { ResourceKey: "SitesNpmErrorTimedOut" }
                        ? "SitesNpmTimedOut"
                        : "SitesNpmFailed"
                );
                AppendCommandOutput(outputBox, NpmErrorMessage(error));
                await DiagnosticLog.WriteFailureAsync(
                    "npm-script",
                    "run",
                    $"The npm script for {site.Name} failed.",
                    error.ToString()
                );
            }
            finally
            {
                running = false;
                progressRing.IsActive = false;
                runButton.IsEnabled = true;
                cancelButton.IsEnabled = false;
                reloadButton.IsEnabled = true;
                scriptBox.IsEnabled = true;
                if (ReferenceEquals(npmCancellation, cancellation)) npmCancellation = null;
            }
        };

        await dialog.ShowAsync();
    }

    private static string NpmErrorMessage(Exception error)
    {
        return error is NpmScriptException npmError
            ? AppLocalization.Format(
                npmError.ResourceKey,
                npmError.ResourceArguments.ToArray()
            )
            : error.Message;
    }

    private static string ArtisanPresetTitleKey(string presetId) => presetId switch
    {
        "route-list" => "SitesArtisanPresetRouteList",
        "migrate-status" => "SitesArtisanPresetMigrationStatus",
        "migrate" => "SitesArtisanPresetMigrate",
        "seed" => "SitesArtisanPresetSeed",
        "migrate-seed" => "SitesArtisanPresetMigrateSeed",
        "optimize" => "SitesArtisanPresetOptimize",
        "optimize-clear" => "SitesArtisanPresetOptimizeClear",
        "cache-clear" => "SitesArtisanPresetCacheClear",
        "config-clear" => "SitesArtisanPresetConfigClear",
        "route-clear" => "SitesArtisanPresetRouteClear",
        "view-clear" => "SitesArtisanPresetViewClear",
        "storage-link" => "SitesArtisanPresetStorageLink",
        "queue-work" => "SitesArtisanPresetQueueWorker",
        "custom" => "SitesArtisanPresetCustom",
        _ => throw new ArgumentOutOfRangeException(nameof(presetId), presetId, null)
    };

    private static void AppendCommandOutput(TextBox outputBox, string value)
    {
        const int maximumCharacters = 1 * 1_024 * 1_024;
        if (value.Length == 0) return;
        var combined = outputBox.Text + value;
        outputBox.Text = combined.Length > maximumCharacters
            ? combined[^maximumCharacters..]
            : combined;
        outputBox.Select(outputBox.Text.Length, 0);
    }

    private void SaveRoots()
    {
        settingsStore.UpdateRoots(Roots);
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "HerdMe",
            Content = message,
            CloseButtonText = AppLocalization.Get("SitesOk")
        };
        await dialog.ShowAsync();
    }
}
