using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HerdMe.Windows.Pages;

public sealed partial class SitesPage
{
    private async void CreateDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        var preferredServiceId = TryCurrentSiteDatabase(site, out var currentService, out _, out _)
            ? currentService.Id : Guid.Empty;
        var services = serviceManager.LoadInstances()
            .Where(instance => SiteDatabaseProvisioner.SupportedDefinitions.Contains(
                instance.DefinitionId
            ))
            .OrderByDescending(instance => instance.Id == preferredServiceId)
            .ThenByDescending(instance => serviceManager.State(instance.Id, instance.DefinitionId)
                == ManagedServiceState.Running)
            .Select(instance => new DatabaseServiceOption(instance))
            .ToArray();
        ManagedServiceInstance? newService = null;
        if (services.Length == 0)
        {
            newService = new ManagedServiceInstance();
            services = [new DatabaseServiceOption(newService)];
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
        var content = new StackPanel { MaxWidth = 420, Spacing = 10 };
        var openInTablePlus = new CheckBox
        {
            Content = AppLocalization.Get("SitesDatabaseOpenAfterCreate"),
            IsChecked = true
        };
        content.Children.Add(serviceBox);
        content.Children.Add(nameBox);
        content.Children.Add(new TextBlock
        {
            Text = Path.Combine(site.Path, ".env"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        });
        content.Children.Add(openInTablePlus);
        content.Children.Add(validationText);
        var progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Visibility = Visibility.Collapsed
        };
        var statusText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        content.Children.Add(progressBar);
        content.Children.Add(statusText);
        var usernameBox = new TextBox
        {
            Header = AppLocalization.Get("SitesDatabaseUsernameField"),
            IsReadOnly = true
        };
        var passwordBox = new TextBox
        {
            Header = AppLocalization.Get("SitesDatabasePasswordField"),
            IsReadOnly = true
        };
        var connectionFields = new StackPanel { Spacing = 8 };
        connectionFields.Children.Add(usernameBox);
        connectionFields.Children.Add(passwordBox);
        var connectionDetails = new Expander
        {
            Header = AppLocalization.Get("SitesDatabaseConnectionDetails"),
            Content = connectionFields,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        content.Children.Add(connectionDetails);
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesDatabaseDialogTitle", site.Name),
            Content = content,
            PrimaryButtonText = AppLocalization.Get("SitesDatabaseCreateAndOpen"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = true
        };
        SiteDatabaseProvisioning? provisioning = null;
        var environmentUpdated = false;
        var configurationCleared = false;
        var busy = false;
        nameBox.TextChanged += (_, _) =>
        {
            var valid = SiteDatabaseProvisioner.IsValidDatabaseName(nameBox.Text.Trim());
            dialog.IsPrimaryButtonEnabled = valid;
            validationText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        };
        void UpdatePrimaryButton()
        {
            dialog.PrimaryButtonText = AppLocalization.Get(provisioning is not null
                ? "SitesDatabaseContinueSetup"
                : openInTablePlus.IsChecked == true
                    ? "SitesDatabaseCreateAndOpen"
                    : "SitesDatabaseCreateAndConnect");
        }
        openInTablePlus.Checked += (_, _) => UpdatePrimaryButton();
        openInTablePlus.Unchecked += (_, _) => UpdatePrimaryButton();
        dialog.Closing += (_, args) => args.Cancel = busy;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (busy || serviceBox.SelectedItem is not DatabaseServiceOption selectedService)
            {
                args.Cancel = true;
                return;
            }
            var deferral = args.GetDeferral();
            using var cancellation = new CancellationTokenSource();
            databaseCancellation = cancellation;
            busy = true;
            serviceBox.IsEnabled = false;
            nameBox.IsEnabled = false;
            openInTablePlus.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false;
            dialog.CloseButtonText = string.Empty;
            progressBar.Visibility = Visibility.Visible;
            statusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "TextFillColorPrimaryBrush"
            ];
            try
            {
                if (provisioning is null)
                {
                    await Task.Run(() => ProjectEnvironmentFile.Load(site.Path));
                    var instance = selectedService.Instance;
                    var instances = serviceManager.LoadInstances().ToList();
                    if (!instances.Any(candidate => candidate.Id == instance.Id))
                    {
                        if (instance != newService)
                            throw new InvalidOperationException(AppLocalization.Get("SitesDatabaseServiceMissing"));
                        instance.Port = WindowsServiceManager.AvailablePort(
                            instance.Port, instances.Select(candidate => candidate.Port)
                        ) ?? throw new InvalidOperationException(
                            AppLocalization.Get("SitesDatabaseNoAvailablePort"));
                        instances.Add(instance);
                        serviceManager.SaveInstances(instances);
                    }
                    if (!serviceManager.IsInstalled(instance.DefinitionId))
                    {
                        statusText.Text = AppLocalization.Format("ServicesInstalling", instance.Name);
                        await serviceManager.InstallAsync(instance.DefinitionId, cancellation.Token);
                    }
                    statusText.Text = AppLocalization.Format("ServicesStarting", instance.Name);
                    await serviceManager.StartAsync(instance.Id, cancellation.Token);
                    statusText.Text = AppLocalization.Get("SitesDatabaseCreating");
                    provisioning = await serviceManager.CreateSiteDatabaseAsync(
                        instance, nameBox.Text.Trim(), cancellation.Token
                    );
                }
                // Retain completed stages so a retry cannot create another database or user.
                if (!environmentUpdated)
                {
                    statusText.Text = AppLocalization.Get("SitesDatabaseSavingEnvironment");
                    await Task.Run(() => serviceManager.AddSiteDatabaseToEnvironment(
                        site.Path, selectedService.Instance, provisioning
                    ));
                    environmentUpdated = true;
                }
                if (!configurationCleared)
                {
                    statusText.Text = AppLocalization.Get("SitesDatabaseApplyingConfiguration");
                    await ClearDatabaseConfigurationCacheAsync(site);
                    configurationCleared = true;
                }
                if (openInTablePlus.IsChecked == true)
                {
                    statusText.Text = AppLocalization.Get("SitesDatabaseOpeningTablePlus");
                    OpenSiteDatabaseInTablePlus(selectedService.Instance, provisioning);
                }
                SiteOperationBar.Title = AppLocalization.Get("SitesDatabaseCreatedTitle");
                SiteOperationBar.Message = AppLocalization.Get("SitesDatabaseEnvironmentReady");
                SiteOperationBar.Severity = InfoBarSeverity.Success;
                SiteOperationBar.IsOpen = true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                args.Cancel = true;
                statusText.Text = AppLocalization.Get("SitesOperationCancelled");
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or NotSupportedException or ArgumentException or TimeoutException
                or System.Net.Http.HttpRequestException)
            {
                args.Cancel = true;
                statusText.Text = (provisioning is null ? string.Empty
                    : AppLocalization.Get(environmentUpdated
                        ? "SitesDatabaseEnvironmentReady"
                        : "SitesDatabaseCreatedStatus") + Environment.NewLine) + error.Message;
                statusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    "SystemFillColorCriticalBrush"
                ];
                if (provisioning is not null && !environmentUpdated)
                {
                    usernameBox.Text = provisioning.Username;
                    passwordBox.Text = provisioning.Password;
                    connectionDetails.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                if (ReferenceEquals(databaseCancellation, cancellation)) databaseCancellation = null;
                busy = false;
                serviceBox.IsEnabled = provisioning is null;
                nameBox.IsEnabled = provisioning is null;
                openInTablePlus.IsEnabled = true;
                progressBar.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = SiteDatabaseProvisioner.IsValidDatabaseName(nameBox.Text.Trim());
                dialog.CloseButtonText = AppLocalization.Get(environmentUpdated ? "SitesDone" : "SitesCancel");
                UpdatePrimaryButton();
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
        if (environmentUpdated && IsSelected(site)) await RefreshSiteDetailsAsync(site);
    }

    private static void OpenSiteDatabaseInTablePlus(
        ManagedServiceInstance instance,
        SiteDatabaseProvisioning provisioning
    )
    {
        TablePlusConnection.Open(TablePlusConnection.UriForDatabase(instance, provisioning)
            ?? throw new NotSupportedException(AppLocalization.Get("SitesDatabaseTablePlusUnavailable")));
    }

    private async Task ClearDatabaseConfigurationCacheAsync(SiteRecord site)
    {
        if (!File.Exists(Path.Combine(site.Path, "artisan"))
            || !File.Exists(Path.Combine(site.Path, "bootstrap", "cache", "config.php"))) return;
        var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
        var result = await ArtisanCommandRunner.RunAsync(
            phpInstaller.PhpExecutable(cycle), site.Path,
            ["config:clear", "--no-ansi", "--no-interaction"],
            composerTools.ManagedEnvironment(cycle), TimeSpan.FromMinutes(2)
        );
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Output);
    }

    private async void OpenDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        if (!TryCurrentSiteDatabase(site, out var instance, out var provisioning, out var error))
        {
            await ShowErrorAsync(error);
            return;
        }
        try
        {
            await serviceManager.StartAsync(instance.Id);
            OpenSiteDatabaseInTablePlus(instance, provisioning);
        }
        catch (Exception openError) when (openError is IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            await ShowErrorAsync(openError.Message);
        }
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
            FlowDirection = AppLocalization.LayoutDirection,
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
        if (TryResolveSiteDatabase(
            values,
            serviceManager.LoadInstances(),
            out instance,
            out provisioning,
            out var serviceMissing
        )) return true;
        if (serviceMissing) error = AppLocalization.Get("SitesDatabaseServiceMissing");
        return false;
    }

    // Pure lookup shared by the UI-thread database actions and the background details probe.
    private static bool TryResolveSiteDatabase(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<ManagedServiceInstance> services,
        out ManagedServiceInstance instance,
        out SiteDatabaseProvisioning provisioning,
        out bool serviceMissing
    )
    {
        instance = null!;
        provisioning = null!;
        serviceMissing = false;
        if (!values.TryGetValue("DB_DATABASE", out var database)
            || !values.TryGetValue("DB_USERNAME", out var username)
            || !values.TryGetValue("DB_PASSWORD", out var password)
            || !values.TryGetValue("DB_PORT", out var portText)
            || !int.TryParse(portText, out var port)
            || !SiteDatabaseProvisioner.IsValidDatabaseName(database)) return false;
        var match = services.FirstOrDefault(service =>
            service.Port == port && SiteDatabaseProvisioner.SupportedDefinitions.Contains(
                service.DefinitionId
            )
        );
        if (match is null)
        {
            serviceMissing = true;
            return false;
        }
        instance = match;
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
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Get("SitesDatabaseRestoreConfirmTitle"),
            Content = AppLocalization.Format("SitesDatabaseRestoreConfirm", provisioning.DatabaseName),
            PrimaryButtonText = AppLocalization.Get("SitesDatabaseRestore"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        await RunDatabaseImportOperationAsync(
            AppLocalization.Get("SitesDatabaseRestoring"),
            async (transferProgress, token) => await serviceManager.RestoreSiteDatabaseAsync(
                instance,
                provisioning,
                file.Path,
                token,
                transferProgress,
                mergeExisting: true
            )
        );
    }

    private async void ImportDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".sql");
        picker.FileTypeFilter.Add(".gz");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

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

        string? configuredDatabase = null;
        int? configuredPort = null;
        try
        {
            var environmentPath = Path.Combine(site.Path, ".env");
            if (File.Exists(environmentPath))
            {
                var values = ParseEnvironment(File.ReadAllText(
                    environmentPath,
                    new UTF8Encoding(false, true)
                ));
                if (values.TryGetValue("DB_DATABASE", out var database)
                    && SiteDatabaseProvisioner.IsValidDatabaseName(database))
                {
                    configuredDatabase = database;
                }
                if (values.TryGetValue("DB_PORT", out var portText)
                    && int.TryParse(portText, out var port)) configuredPort = port;
            }
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException or DecoderFallbackException)
        {
            await ShowErrorAsync(error.Message);
            return;
        }

        var serviceBox = new ComboBox
        {
            Header = AppLocalization.Get("SitesDatabaseServiceField"),
            ItemsSource = services,
            SelectedIndex = Math.Max(0, Array.FindIndex(services, option =>
                option.Instance.Port == configuredPort)),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var nameBox = new TextBox
        {
            Header = AppLocalization.Get("SitesDatabaseNameField"),
            Text = configuredDatabase ?? SiteDatabaseProvisioner.SuggestedDatabaseName(site.Name),
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
        var targetContent = new StackPanel { Width = 430, Spacing = 10 };
        targetContent.Children.Add(new TextBlock
        {
            Text = AppLocalization.Get("SitesDatabaseImportTargetDescription"),
            TextWrapping = TextWrapping.Wrap
        });
        targetContent.Children.Add(serviceBox);
        targetContent.Children.Add(nameBox);
        targetContent.Children.Add(validationText);
        var targetDialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Get("SitesDatabaseImportTargetTitle"),
            Content = targetContent,
            PrimaryButtonText = AppLocalization.Get("SitesContinue"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        nameBox.TextChanged += (_, _) =>
        {
            var valid = SiteDatabaseProvisioner.IsValidDatabaseName(nameBox.Text.Trim());
            targetDialog.IsPrimaryButtonEnabled = valid;
            validationText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        };
        if (await targetDialog.ShowAsync() != ContentDialogResult.Primary
            || serviceBox.SelectedItem is not DatabaseServiceOption selectedService) return;

        var databaseName = nameBox.Text.Trim();
        bool databaseExists;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            databaseExists = await serviceManager.SiteDatabaseExistsAsync(
                selectedService.Instance,
                databaseName,
                timeout.Token
            );
        }
        catch (Exception error) when (error is IOException or InvalidOperationException
            or ArgumentException or OperationCanceledException or NotSupportedException)
        {
            await ShowErrorAsync(error is OperationCanceledException
                ? AppLocalization.Get("SitesDatabaseInspectionTimedOut")
                : error.Message);
            return;
        }

        var confirmation = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = databaseExists
                ? AppLocalization.Get("SitesDatabaseImportExistingTitle")
                : AppLocalization.Get("SitesDatabaseImportCreateTitle"),
            Content = AppLocalization.Format(
                databaseExists
                    ? selectedService.Instance.DefinitionId is "mysql" or "mariadb"
                        ? "SitesDatabaseImportExistingWarning"
                        : "SitesDatabaseImportExistingPostgreSqlWarning"
                    : "SitesDatabaseImportCreateMessage",
                databaseName
            ),
            PrimaryButtonText = AppLocalization.Get("SitesDatabaseImport"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

        SiteDatabaseProvisioning? created = null;
        var succeeded = await RunDatabaseImportOperationAsync(
            AppLocalization.Get("SitesDatabaseRestoring"),
            async (transferProgress, token) =>
            {
                if (databaseExists)
                {
                    await serviceManager.RestoreSiteDatabaseAsAdministratorAsync(
                        selectedService.Instance,
                        databaseName,
                        file.Path,
                        token,
                        transferProgress,
                        mergeExisting: true
                    );
                    return;
                }

                created = await serviceManager.CreateSiteDatabaseAsync(
                    selectedService.Instance,
                    databaseName,
                    token
                );
                try
                {
                    await serviceManager.RestoreSiteDatabaseAsync(
                        selectedService.Instance,
                        created,
                        file.Path,
                        token,
                        transferProgress
                    );
                    serviceManager.AddSiteDatabaseToEnvironment(
                        site.Path,
                        selectedService.Instance,
                        created
                    );
                }
                catch
                {
                    try
                    {
                        await serviceManager.DeleteSiteDatabaseAsync(
                            selectedService.Instance,
                            created,
                            CancellationToken.None
                        );
                    }
                    catch (Exception)
                    {
                    }
                    throw;
                }
            }
        );
        if (succeeded && created is not null && IsSelected(site))
        {
            await RefreshSiteDetailsAsync(site);
        }
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
            FlowDirection = AppLocalization.LayoutDirection,
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
}
