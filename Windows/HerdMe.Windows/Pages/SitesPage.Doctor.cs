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
    private async void SiteDoctor_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        string cycle;
        IReadOnlyList<SiteHealthCheck> checks;
        HealthDetailsText.Text = AppLocalization.Get("SitesDetailsChecking");
        try
        {
            cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
            // The inspection probes PHP, Composer, certificates, and project files; keep it
            // off the UI thread so the window stays responsive.
            checks = await Task.Run(() => SiteHealthInspector.InspectAsync(
                site.Path,
                site.Domain,
                cycle,
                phpInstaller,
                composerTools,
                certificates,
                site.NodeVersion
            ));
        }
        catch (Exception error)
        {
            HealthDetailsText.Text = AppLocalization.Get("SitesDoctorUnavailable");
            await DiagnosticLog.WriteFailureAsync(
                "site-doctor",
                "inspect",
                "Site Doctor could not inspect the selected project.",
                error.ToString()
            );
            await ShowErrorAsync(AppLocalization.Format(
                "SitesDoctorFailed",
                UserErrorPresentation.Describe(error)
            ));
            return;
        }
        if (!loaded) return;
        HealthDetailsText.Text = AppLocalization.Format(
            "SitesHealthSummary",
            checks.Count(check => check.Healthy),
            checks.Count
        );
        using var repairCancellation = new CancellationTokenSource();
        var checkRows = new StackPanel { Spacing = 12 };
        foreach (var check in checks)
        {
            var panel = new StackPanel { Spacing = 4 };
            var label = new TextBlock { Text = DoctorCheckRow(check), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(label);
            if (!check.Healthy && check.Name is "PHP" or "PHP extensions" or "Composer" or "HTTPS")
            {
                var repair = new Button { Content = new SymbolIcon(Symbol.Repair), HorizontalAlignment = HorizontalAlignment.Left };
                ToolTipService.SetToolTip(repair, AppLocalization.Get("SitesRepair"));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(repair, AppLocalization.Get("SitesRepair") + " " + DoctorCheckName(check.Name));
                repair.Click += async (_, _) =>
                {
                    repair.IsEnabled = false;
                    try
                    {
                        switch (check.Name)
                        {
                            case "PHP": await phpInstaller.InstallAsync(cycle, repairCancellation.Token); break;
                            case "PHP extensions": await phpInstaller.EnsureManagedConfigurationAsync(cycle, repairCancellation.Token); break;
                            case "Composer": await composerTools.InstallOrUpdateAsync(cycle, repairCancellation.Token); break;
                            case "HTTPS": certificates.TrustAuthority(); break;
                        }
                        var refreshed = await Task.Run(() => SiteHealthInspector.InspectAsync(site.Path, site.Domain, cycle, phpInstaller, composerTools,
                            certificates, site.NodeVersion, repairCancellation.Token));
                        var result = refreshed.First(item => item.Name == check.Name);
                        label.Text = DoctorCheckRow(result);
                        repair.IsEnabled = !result.Healthy;
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error) { label.Text = UserErrorPresentation.Describe(error); repair.IsEnabled = true; }
                };
                panel.Children.Add(repair);
            }
            checkRows.Children.Add(panel);
        }
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesDoctorTitle", site.Name),
            Content = new ScrollViewer { Content = checkRows, MaxHeight = 360 },
            PrimaryButtonText = AppLocalization.Get("SitesRepair"),
            CloseButtonText = AppLocalization.Get("SitesDone")
        };
        dialog.Closed += (_, _) => repairCancellation.Cancel();
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
                var repairedChecks = await Task.Run(() => SiteHealthInspector.InspectAsync(
                    site.Path,
                    site.Domain,
                    cycle,
                    phpInstaller,
                    composerTools,
                    certificates,
                    site.NodeVersion,
                    cancellationToken
                ), cancellationToken);
                HealthDetailsText.Text = AppLocalization.Format(
                    "SitesHealthSummary",
                    repairedChecks.Count(check => check.Healthy),
                    repairedChecks.Count
                );
                await RefreshSiteDetailsAsync(site);
            }
        );
    }

    private static string DoctorCheckRow(SiteHealthCheck check) => AppLocalization.Format(
        check.Healthy ? "SitesDoctorCheckPassed" : "SitesDoctorCheckAttention",
        DoctorCheckName(check.Name),
        check.Detail
    );

    private static string DoctorCheckName(string name) => name switch
    {
        "Project" => AppLocalization.Get("DoctorProject"),
        "Environment" => AppLocalization.Get("DoctorEnvironment"),
        "Dependencies" => AppLocalization.Get("DoctorDependencies"),
        "PHP extensions" => AppLocalization.Get("DoctorExtensions"),
        "Storage directories" => AppLocalization.Get("DoctorStorage"),
        "Application key" => AppLocalization.Get("DoctorKey"),
        "Database configuration" => AppLocalization.Get("DoctorDatabase"),
        _ => name
    };

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
}
