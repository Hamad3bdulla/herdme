using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

/// <summary>
/// herdme.yml: apply a project's committed settings (runtimes, services, database) and export
/// the current setup so teammates can reproduce it.
/// </summary>
public sealed partial class SitesPage
{
    private async void ApplyManifest_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site || XamlRoot is null) return;
        ProjectManifest? manifest;
        try
        {
            manifest = await Task.Run(() => ProjectManifestFile.Load(site.Path));
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
        {
            await ShowErrorAsync(error.Message);
            return;
        }
        if (manifest is null || manifest.IsEmpty)
        {
            await ShowErrorAsync(AppLocalization.Get(manifest is null
                ? "SitesManifestMissing"
                : "SitesManifestEmpty"));
            return;
        }
        await ShowApplyManifestDialogAsync(site, manifest);
    }

    private async Task ShowApplyManifestDialogAsync(SiteRecord site, ProjectManifest manifest)
    {
        var content = new StackPanel { Spacing = 10, MinWidth = 380 };
        content.Children.Add(new TextBlock
        {
            Text = AppLocalization.Get("SitesManifestApplyIntro"),
            TextWrapping = TextWrapping.WrapWholeWords
        });
        foreach (var line in ManifestSummary(manifest))
        {
            content.Children.Add(new TextBlock
            {
                Text = "\u2022 " + line,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
        }
        var progressBar = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var statusText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var errorBar = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            IsClosable = false,
            IsOpen = false
        };
        content.Children.Add(progressBar);
        content.Children.Add(statusText);
        content.Children.Add(errorBar);
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesManifestApplyTitle", site.Name),
            Content = new ScrollViewer { Content = content, MaxHeight = 480 },
            PrimaryButtonText = AppLocalization.Get("SitesManifestApplyButton"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        var busy = false;
        var applied = false;
        var databaseDone = false;
        var notes = new List<string>();
        dialog.Closing += (_, args) => args.Cancel = busy;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (applied || busy)
            {
                args.Cancel = busy;
                return;
            }
            args.Cancel = true;
            var deferral = args.GetDeferral();
            busy = true;
            dialog.IsPrimaryButtonEnabled = false;
            dialog.CloseButtonText = string.Empty;
            progressBar.Visibility = Visibility.Visible;
            errorBar.IsOpen = false;
            try
            {
                notes.Clear();
                await ApplyManifestAsync(site, manifest, text => statusText.Text = text, notes, () => databaseDone,
                    () => databaseDone = true);
                applied = true;
                statusText.Text = notes.Count == 0
                    ? AppLocalization.Get("SitesManifestApplied")
                    : AppLocalization.Get("SitesManifestApplied") + Environment.NewLine
                        + string.Join(Environment.NewLine, notes);
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or NotSupportedException or ArgumentException or TimeoutException
                or System.Net.Http.HttpRequestException)
            {
                statusText.Text = string.Empty;
                errorBar.Message = UserErrorPresentation.Describe(error);
                errorBar.IsOpen = true;
            }
            finally
            {
                busy = false;
                progressBar.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = !applied;
                dialog.PrimaryButtonText = AppLocalization.Get(applied
                    ? "SitesManifestApplyButton"
                    : "SitesManifestRetryButton");
                dialog.CloseButtonText = AppLocalization.Get(applied ? "SitesDone" : "SitesCancel");
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
        if (applied)
        {
            detectionCache.Invalidate(site.Path);
            await ScanAsync();
        }
    }

    private static IEnumerable<string> ManifestSummary(ProjectManifest manifest)
    {
        if (manifest.Php is { } php) yield return AppLocalization.Format("SitesManifestSummaryPhp", php);
        if (manifest.Node is { } node) yield return AppLocalization.Format("SitesManifestSummaryNode", node);
        if (manifest.Services.Count > 0)
        {
            yield return AppLocalization.Format(
                "SitesManifestSummaryServices",
                string.Join(", ", manifest.Services.Select(id => ManagedServiceCatalog.Get(id).Name))
            );
        }
        if (manifest.Database is { } database)
        {
            yield return AppLocalization.Format("SitesManifestSummaryDatabase", database);
        }
    }

    private async Task ApplyManifestAsync(
        SiteRecord site,
        ProjectManifest manifest,
        Action<string> report,
        List<string> notes,
        Func<bool> databaseDone,
        Action markDatabaseDone
    )
    {
        if (manifest.Php is { } php)
        {
            if (!PhpRuntimeInstaller.IsSupportedCycle(php))
            {
                throw new NotSupportedException(AppLocalization.Format("SitesManifestPhpUnsupported", php));
            }
            if (!phpInstaller.IsInstalled(php))
            {
                report(AppLocalization.Format("SitesManifestInstallingRuntime", "PHP " + php));
                await phpInstaller.InstallAsync(php);
            }
            await Task.Run(() => siteRuntimeStore.SetPhp(site.Path, php));
        }
        if (manifest.Node is { } node)
        {
            var major = node.Split('.')[0];
            var pin = nodeInstaller.InstalledVersions().FirstOrDefault(version => version == node)
                ?? nodeInstaller.InstalledVersion(major);
            if (pin is null)
            {
                report(AppLocalization.Format("SitesManifestInstallingRuntime", "Node.js " + major));
                pin = (await nodeInstaller.InstallAsync(major)).Version;
            }
            if (node.Count(character => character == '.') == 2 && pin != node)
            {
                notes.Add(AppLocalization.Format("SitesManifestNodeSubstituted", node, pin));
            }
            var nodePin = pin;
            await Task.Run(() => siteRuntimeStore.SetNode(site.Path, nodePin));
        }
        ManagedServiceInstance? databaseInstance = null;
        foreach (var id in manifest.Services)
        {
            var definition = ManagedServiceCatalog.Get(id);
            if (!definition.IsInstallable)
            {
                throw new NotSupportedException(definition.UnavailableReason
                    ?? AppLocalization.Format("SitesManifestServiceUnavailable", definition.Name));
            }
            var instances = serviceManager.LoadInstances().ToList();
            var instance = instances.FirstOrDefault(candidate =>
                candidate.DefinitionId.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
            if (instance is null)
            {
                instance = new ManagedServiceInstance
                {
                    DefinitionId = definition.Id,
                    Name = definition.Name,
                    Port = WindowsServiceManager.AvailablePort(
                        definition.DefaultPort, instances.Select(candidate => candidate.Port)
                    ) ?? throw new InvalidOperationException(AppLocalization.Get("SitesDatabaseNoAvailablePort")),
                    StartAutomatically = true
                };
                instances.Add(instance);
                serviceManager.SaveInstances(instances);
            }
            if (!serviceManager.IsInstalled(definition.Id))
            {
                report(AppLocalization.Format("ServicesInstalling", instance.Name));
                await serviceManager.InstallAsync(definition.Id);
            }
            if (serviceManager.State(instance.Id, instance.DefinitionId) != ManagedServiceState.Running)
            {
                report(AppLocalization.Format("ServicesStarting", instance.Name));
                await serviceManager.StartAsync(instance.Id);
            }
            if (string.Equals(id, manifest.DatabaseService, StringComparison.OrdinalIgnoreCase))
            {
                databaseInstance = instance;
            }
        }
        if (manifest.Database is { } database && databaseInstance is not null && !databaseDone())
        {
            report(AppLocalization.Get("SitesDatabaseCreating"));
            if (await serviceManager.SiteDatabaseExistsAsync(databaseInstance, database))
            {
                notes.Add(AppLocalization.Format("SitesManifestDatabaseExists", database));
            }
            else
            {
                await Task.Run(() => ProjectEnvironmentFile.Load(site.Path));
                var provisioning = await serviceManager.CreateSiteDatabaseAsync(databaseInstance, database);
                report(AppLocalization.Get("SitesDatabaseSavingEnvironment"));
                var instance = databaseInstance;
                await Task.Run(() => serviceManager.AddSiteDatabaseToEnvironment(site.Path, instance, provisioning));
                report(AppLocalization.Get("SitesDatabaseApplyingConfiguration"));
                await ClearDatabaseConfigurationCacheAsync(site);
            }
            markDatabaseDone();
        }
    }

    private async void ExportManifest_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site || XamlRoot is null) return;
        string text;
        bool exists;
        try
        {
            (text, exists) = await Task.Run(() =>
            {
                var configured = serviceManager.LoadInstances().Select(instance => instance.DefinitionId);
                string? environmentContents = null;
                try
                {
                    environmentContents = ProjectEnvironmentFile.Load(site.Path).Contents;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    or InvalidDataException)
                {
                }
                var existing = ProjectManifestFile.Exists(site.Path);
                var current = existing
                    ? File.ReadAllText(ProjectManifestFile.PathFor(site.Path))
                    : ProjectManifestFile.Serialize(ProjectManifestFile.Suggest(
                        siteRuntimeStore.GetPhp(site.Path),
                        siteRuntimeStore.GetNode(site.Path),
                        environmentContents,
                        configured
                    ));
                return (current, existing);
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await ShowErrorAsync(error.Message);
            return;
        }
        var editor = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FlowDirection = FlowDirection.LeftToRight,
            MinHeight = 220,
            MaxHeight = 360,
            IsSpellCheckEnabled = false
        };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        var errorBar = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false };
        var content = new StackPanel { Spacing = 10, MinWidth = 420 };
        content.Children.Add(new TextBlock
        {
            Text = AppLocalization.Get(exists ? "SitesManifestExportExisting" : "SitesManifestExportIntro"),
            TextWrapping = TextWrapping.WrapWholeWords
        });
        content.Children.Add(editor);
        content.Children.Add(errorBar);
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesManifestExportTitle", site.Name),
            Content = content,
            PrimaryButtonText = AppLocalization.Get("SitesSave"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        var saved = false;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var manifest = ProjectManifestFile.Parse(editor.Text.Replace("\r", string.Empty));
                ProjectManifestFile.Save(site.Path, manifest);
                saved = true;
            }
            catch (Exception error) when (error is FormatException or IOException
                or UnauthorizedAccessException)
            {
                args.Cancel = true;
                errorBar.Message = error.Message;
                errorBar.IsOpen = true;
            }
        };
        await dialog.ShowAsync();
        if (!saved) return;
        SiteOperationBar.Title = AppLocalization.Get("SitesManifestSavedTitle");
        SiteOperationBar.Message = ProjectManifestFile.PathFor(site.Path);
        SiteOperationBar.Severity = InfoBarSeverity.Success;
        SiteOperationBar.IsOpen = true;
    }
}
