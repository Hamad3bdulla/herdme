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
    private async Task<bool> RunDatabaseImportOperationAsync(
        string title,
        Func<IProgress<DatabaseTransferProgress>, CancellationToken, Task> operation
    )
    {
        siteOperationCancellation?.Cancel();
        siteOperationCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        siteOperationCancellation = cancellation;
        SiteOperationBar.Title = title;
        SiteOperationBar.Message = AppLocalization.Get("SitesDatabaseProgressStarting");
        SiteOperationBar.Severity = InfoBarSeverity.Informational;
        SiteOperationBar.IsOpen = true;
        SiteOperationCancelButton.Visibility = Visibility.Visible;
        SiteOperationProgress.IsIndeterminate = false;
        SiteOperationProgress.Value = 0;
        SiteOperationProgress.Visibility = Visibility.Visible;
        var progress = new Progress<DatabaseTransferProgress>(value =>
        {
            SiteOperationProgress.Value = value.Percentage;
            var remaining = value.EstimatedRemaining is { } estimate
                ? FormatDuration(estimate)
                : AppLocalization.Get("SitesDatabaseProgressCalculating");
            SiteOperationBar.Message = AppLocalization.Format(
                "SitesDatabaseProgress",
                value.Percentage,
                FormatTransferSize(value.BytesTransferred),
                FormatTransferSize(value.TotalBytes),
                remaining
            );
            if (value.CompatibilityFixes > 0)
            {
                SiteOperationBar.Message += Environment.NewLine + AppLocalization.Format(
                    "SitesDatabaseCompatibilityFixes",
                    value.CompatibilityFixes
                );
            }
        });
        try
        {
            await operation(progress, cancellation.Token);
            SiteOperationProgress.Value = 100;
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
            SiteOperationCancelButton.Visibility = Visibility.Collapsed;
            SiteOperationProgress.Visibility = Visibility.Collapsed;
            SiteOperationProgress.IsIndeterminate = true;
        }
    }

    private static string FormatTransferSize(long bytes) => bytes switch
    {
        < 1_024 => $"{bytes} B",
        < 1_048_576 => $"{bytes / 1_024d:F1} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576d:F1} MB",
        _ => $"{bytes / 1_073_741_824d:F1} GB"
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
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
        SiteOperationLogText.Text = string.Empty;
        SiteOperationLogPanel.Visibility = Visibility.Visible;
        SiteOperationCancelButton.Visibility = Visibility.Visible;
        SiteOperationProgress.IsIndeterminate = true;
        SiteOperationProgress.Visibility = Visibility.Visible;
        var progress = new Progress<string>(text =>
        {
            AppendSiteOperationOutput(text);
            SiteOperationBar.Message = LatestOperationStatus(text);
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
            SiteOperationBar.Message = LatestOperationStatus(error.Message);
            AppendSiteOperationOutput(error.Message + Environment.NewLine);
            return false;
        }
        finally
        {
            if (ReferenceEquals(siteOperationCancellation, cancellation))
            {
                siteOperationCancellation = null;
            }
            cancellation.Dispose();
            SiteOperationCancelButton.Visibility = Visibility.Collapsed;
            SiteOperationProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void AppendSiteOperationOutput(string value)
    {
        const int maximumCharacters = 128 * 1_024;
        if (string.IsNullOrEmpty(value)) return;
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var combined = SiteOperationLogText.Text + normalized;
        SiteOperationLogText.Text = combined.Length > maximumCharacters
            ? combined[^maximumCharacters..]
            : combined;
        SiteOperationLogText.Select(SiteOperationLogText.Text.Length, 0);
    }

    private static string LatestOperationStatus(string value)
    {
        const int maximumCharacters = 240;
        var latest = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
        return latest.Length > maximumCharacters
            ? latest[..maximumCharacters] + "..."
            : latest;
    }

    private async Task ShowCommandResultAsync(string title, string output, bool success)
    {
        var outputBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(output)
                ? AppLocalization.Get("SitesNoCommandOutput")
                : output,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Width = 500,
            Height = 320
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(outputBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(outputBox, ScrollBarVisibility.Auto);
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = title,
            Content = outputBox,
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
        var console = new CommandConsole(
            "SitesArtisanReady",
            "SitesArtisanOutputPlaceholder",
            "SitesArtisanCancelling"
        );
        var content = new StackPanel { Spacing = 12, Width = 500 };
        content.Children.Add(favorites);
        content.Children.Add(presetBox);
        content.Children.Add(customBox);
        console.AddTo(content);
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesArtisanDialogTitle", site.Name),
            Content = content,
            CloseButtonText = AppLocalization.Get("SitesClose")
        };
        console.AttachTo(dialog);
        using var suggestionCancellation = new CancellationTokenSource();
        dialog.Closing += (_, _) => suggestionCancellation.Cancel();
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

        console.RunButton.Click += async (_, _) =>
        {
            if (console.IsRunning) return;
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
                console.StatusText.Text = AppLocalization.Get("SitesArtisanFailed");
                console.OutputBox.Text = error.Message;
                return;
            }

            await console.RunAsync(
                AppLocalization.Get("SitesArtisanValidatingPhp"),
                "SitesArtisanCancelled",
                enabled =>
                {
                    presetBox.IsEnabled = enabled;
                    customBox.IsEnabled = enabled;
                },
                async cancellationToken =>
                {
                    try
                    {
                        var cycle = site.PhpVersion ?? runtimePolicy.Load().PhpCycle;
                        var php = phpInstaller.PhpExecutable(cycle);
                        await runtimePolicy.PrepareLaunchAsync(php, cycle, cancellationToken);
                        var environmentVariables = composerTools.ManagedEnvironment(cycle);
                        console.StatusText.Text = AppLocalization.Get("SitesArtisanRunning");
                        var result = await ArtisanCommandRunner.RunAsync(
                            php,
                            site.Path,
                            AnsiParser.WithArtisanColors(command.Arguments),
                            environmentVariables,
                            command.Timeout,
                            console.OutputProgress(),
                            cancellationToken
                        );
                        console.StatusText.Text = result.ExitCode == 0
                            ? AppLocalization.Get("SitesArtisanCompleted")
                            : AppLocalization.Format("SitesArtisanFailedExit", result.ExitCode);
                        if (console.OutputBox.Text.Length == 0) console.OutputBox.Text = result.Output;
                    }
                    catch (Exception error) when (error is not OperationCanceledException
                        || !cancellationToken.IsCancellationRequested)
                    {
                        console.StatusText.Text = AppLocalization.Get(
                            error is TimeoutException ? "SitesArtisanTimedOut" : "SitesArtisanFailed"
                        );
                        console.Append(error.Message);
                        await DiagnosticLog.WriteFailureAsync(
                            "artisan",
                            "run",
                            $"The Artisan command for {site.Name} failed.",
                            error.ToString()
                        );
                    }
                }
            );
        };

        activeCommandConsole = console;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            if (ReferenceEquals(activeCommandConsole, console)) activeCommandConsole = null;
        }
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

        var console = new CommandConsole(
            "SitesNpmReady",
            "SitesNpmOutputPlaceholder",
            "SitesNpmCancelling"
        );
        var content = new StackPanel { Spacing = 12, Width = 500 };
        content.Children.Add(favorites);
        content.Children.Add(scriptRow);
        console.AddTo(content);
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesNpmDialogTitle", site.Name),
            Content = content,
            CloseButtonText = AppLocalization.Get("SitesClose")
        };
        console.AttachTo(dialog);

        reloadButton.Click += async (_, _) =>
        {
            if (console.IsRunning) return;
            reloadButton.IsEnabled = false;
            scriptBox.IsEnabled = false;
            console.SetBusy(true);
            console.StatusText.Text = AppLocalization.Get("SitesNpmLoading");
            try
            {
                var reloaded = await Task.Run(() => NpmScriptCatalog.Discover(site.Path));
                scriptSuggestions = reloaded.Select(script => script.Name).ToArray();
                scriptBox.ItemsSource = scriptSuggestions;
                scriptBox.Text = scriptSuggestions[0];
                console.StatusText.Text = AppLocalization.Get("SitesNpmReady");
                console.OutputBox.Text = string.Empty;
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or ArgumentException
                or NpmScriptException)
            {
                scriptBox.ItemsSource = Array.Empty<DisplayOption>();
                console.StatusText.Text = AppLocalization.Get("SitesNpmUnavailable");
                console.OutputBox.Text = NpmErrorMessage(error);
            }
            finally
            {
                console.SetBusy(false);
                reloadButton.IsEnabled = true;
                scriptBox.IsEnabled = true;
            }
        };
        console.RunButton.Click += async (_, _) =>
        {
            if (console.IsRunning) return;
            var selectedScript = scriptBox.Text.Trim();
            if (selectedScript.Length == 0) return;

            await console.RunAsync(
                AppLocalization.Get("SitesNpmPreparingNode"),
                "SitesNpmCancelled",
                enabled =>
                {
                    reloadButton.IsEnabled = enabled;
                    scriptBox.IsEnabled = enabled;
                },
                async cancellationToken =>
                {
                    try
                    {
                        var invocation = NpmScriptRunner.CreateInvocation(
                            nodeInstaller,
                            site.Path,
                            site.NodeVersion,
                            selectedScript
                        );
                        console.StatusText.Text = AppLocalization.Get("SitesNpmRunning");
                        var result = await NpmScriptRunner.RunAsync(
                            invocation,
                            console.OutputProgress(),
                            cancellationToken
                        );
                        console.StatusText.Text = result.ExitCode == 0
                            ? AppLocalization.Get("SitesNpmCompleted")
                            : AppLocalization.Format("SitesNpmFailedExit", result.ExitCode);
                        if (console.OutputBox.Text.Length == 0) console.OutputBox.Text = result.Output;
                    }
                    catch (Exception error) when (error is not OperationCanceledException
                        || !cancellationToken.IsCancellationRequested)
                    {
                        console.StatusText.Text = AppLocalization.Get(
                            error is NpmScriptException { ResourceKey: "SitesNpmErrorTimedOut" }
                                ? "SitesNpmTimedOut"
                                : "SitesNpmFailed"
                        );
                        console.Append(NpmErrorMessage(error));
                        await DiagnosticLog.WriteFailureAsync(
                            "npm-script",
                            "run",
                            $"The npm script for {site.Name} failed.",
                            error.ToString()
                        );
                    }
                }
            );
        };

        activeCommandConsole = console;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            if (ReferenceEquals(activeCommandConsole, console)) activeCommandConsole = null;
        }
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
}
