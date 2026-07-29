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

    private readonly CoreClient coreClient;
    private readonly WindowsLocalEnvironment environment;
    private readonly SiteConfigurationStore settingsStore;
    private readonly LaravelProjectCreator projectCreator;
    private CancellationTokenSource? projectCreationCancellation;
    private readonly SiteRuntimeStore siteRuntimeStore;
    private readonly PhpRuntimeInstaller phpInstaller;
    private readonly PhpRuntimePolicy runtimePolicy;
    private readonly NodeRuntimeInstaller nodeInstaller;
    private readonly ComposerToolManager composerTools;
    private readonly WindowsServiceManager serviceManager;
    private readonly SiteScanGeneration siteScanGeneration = new();
    private bool loaded;
    private bool suppressPreviewToggle = true;
    private SiteRecord? selectedSite;
    private CancellationTokenSource? artisanCancellation;
    private CancellationTokenSource? npmCancellation;
    private CancellationTokenSource? siteDetailsCancellation;
    private CancellationTokenSource? gitInspectionCancellation;

    public ObservableCollection<string> Roots { get; } = [];
    public ObservableCollection<SiteRecord> Sites { get; } = [];
    public ObservableCollection<SiteRecord> VisibleSites { get; } = [];

    public SitesPage(
        CoreClient coreClient,
        WindowsLocalEnvironment environment,
        SiteConfigurationStore settingsStore,
        LaravelProjectCreator projectCreator,
        SiteRuntimeStore siteRuntimeStore,
        PhpRuntimeInstaller phpInstaller,
        PhpRuntimePolicy runtimePolicy,
        NodeRuntimeInstaller nodeInstaller,
        ComposerToolManager composerTools,
        WindowsServiceManager serviceManager
    )
    {
        this.coreClient = coreClient;
        this.environment = environment;
        this.settingsStore = settingsStore;
        this.projectCreator = projectCreator;
        this.siteRuntimeStore = siteRuntimeStore;
        this.phpInstaller = phpInstaller;
        this.runtimePolicy = runtimePolicy;
        this.nodeInstaller = nodeInstaller;
        this.composerTools = composerTools;
        this.serviceManager = serviceManager;
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
        var wasRunning = environment.IsRunning;
        try
        {
            if (wasRunning) await environment.StopAsync();
            var php = (phpBox.SelectedItem as DisplayOption)?.Value;
            var node = (nodeBox.SelectedItem as DisplayOption)?.Value;
            siteRuntimeStore.SetPhp(path, string.IsNullOrEmpty(php) ? null : php);
            siteRuntimeStore.SetNode(path, string.IsNullOrEmpty(node) ? null : node);
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
        UrlButton.Content = SitePresentation.DisplayAddress(
            site,
            environment.IsRunning,
            environment.HttpPort,
            environment.HttpsPort
        );
        _ = RefreshSiteDetailsAsync(site);
        _ = RefreshPreviewAsync();
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
        foreach (var line in contents.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
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
        var customBox = new TextBox
        {
            Header = AppLocalization.Get("SitesArtisanCustomCommandField"),
            PlaceholderText = "route:list --path=api",
            Visibility = Visibility.Collapsed
        };
        presetBox.SelectionChanged += (_, _) =>
        {
            customBox.Visibility = (presetBox.SelectedItem as DisplayOption)?.Value == "custom"
                ? Visibility.Visible
                : Visibility.Collapsed;
        };
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
            MinHeight = 280,
            MaxHeight = 420,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        ScrollViewer.SetVerticalScrollBarVisibility(outputBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(outputBox, ScrollBarVisibility.Auto);
        var runButton = new Button { Content = AppLocalization.Get("SitesRun") };
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
        var content = new StackPanel { Spacing = 12, MinWidth = 620 };
        content.Children.Add(presetBox);
        content.Children.Add(customBox);
        content.Children.Add(statusRow);
        content.Children.Add(outputBox);
        content.Children.Add(buttons);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesArtisanDialogTitle", site.Name),
            Content = content,
            CloseButtonText = AppLocalization.Get("SitesClose")
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

        var scriptBox = new ComboBox
        {
            Header = AppLocalization.Get("SitesNpmScriptField"),
            ItemsSource = discoveredScripts.Select(script => new DisplayOption(script.Name, script.Name)).ToArray(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 520
        };
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
            MinHeight = 280,
            MaxHeight = 420,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        ScrollViewer.SetVerticalScrollBarVisibility(outputBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(outputBox, ScrollBarVisibility.Auto);
        var runButton = new Button { Content = AppLocalization.Get("SitesRun") };
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
        var content = new StackPanel { Spacing = 12, MinWidth = 620 };
        content.Children.Add(scriptRow);
        content.Children.Add(statusRow);
        content.Children.Add(outputBox);
        content.Children.Add(buttons);
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
                scriptBox.ItemsSource = reloaded
                    .Select(script => new DisplayOption(script.Name, script.Name))
                    .ToArray();
                scriptBox.SelectedIndex = 0;
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
            if (running || scriptBox.SelectedItem is not DisplayOption selectedScript) return;

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
                    selectedScript.Value
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
