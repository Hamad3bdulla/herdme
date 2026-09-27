using System.ComponentModel;
using System.Diagnostics;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

// The "Getting started" card (Services/GettingStarted.cs) and the startup snapshot: the last
// known counts are shown at once, marked as updating, until the first refresh replaces them.
public sealed partial class DashboardPage
{
    private IReadOnlyList<SiteRecord> gettingStartedSites = [];
    private int gettingStartedMail;
    private int gettingStartedDumps;
    private string? gettingStartedSignature;

    private void ShowSnapshotCounts()
    {
        if (summaryCountsShown) return;
        StartupSnapshotData? snapshot;
        try
        {
            snapshot = startupSnapshot?.Load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            snapshot = null;
        }
        if (snapshot is not { ServiceCount: { } services, MailCount: { } mail, DumpCount: { } dumps }) return;
        var updating = AppLocalization.Get("DashboardUpdating");
        var neutral = StatusStyles.Dot(StatusTone.Neutral);
        SitesCountText.Text = snapshot.Sites.Count.ToString();
        ServicesCountText.Text = services.ToString();
        MailCountText.Text = mail.ToString();
        DumpsCountText.Text = dumps.ToString();
        foreach (var text in new[] { SitesStatusText, ServicesStatusText, MailStatusText, DumpsStatusText })
        {
            text.Text = updating;
        }
        foreach (var dot in new[] { SitesStatusDot, ServicesStatusDot, MailStatusDot, DumpsStatusDot })
        {
            dot.Style = neutral;
        }
        ShowSummaryCounts();
    }

    private void SaveStartupCounts(int services, int mail, int dumps, int healthIssues)
    {
        if (startupSnapshot is not { } store) return;
        _ = Task.Run(() =>
        {
            try
            {
                store.SaveCounts(services, mail, dumps, healthIssues);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Only a cache; the next refresh tries again.
            }
        });
    }

    private void UpdateGettingStarted(
        WindowsSiteSettings settings,
        IReadOnlyList<SiteRecord> sites,
        int mailCount,
        int dumpCount
    )
    {
        gettingStartedSites = sites;
        gettingStartedMail = mailCount;
        gettingStartedDumps = dumpCount;
        var steps = GettingStarted.Evaluate(settings.GettingStartedSteps, sites.Count, mailCount, dumpCount);
        if (!GettingStarted.ShouldShow(settings.GettingStartedDismissed, steps))
        {
            GettingStartedCard.Visibility = Visibility.Collapsed;
            return;
        }
        var done = GettingStarted.DoneCount(steps);
        GettingStartedProgress.Maximum = steps.Count;
        GettingStartedProgress.Value = done;
        GettingStartedProgressText.Text = AppLocalization.Format("DashboardGettingStartedProgress", done, steps.Count);
        AutomationProperties.SetName(GettingStartedProgress, GettingStartedProgressText.Text);
        GettingStartedCard.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(GettingStartedDismissButton, AppLocalization.Get("DashboardGettingStartedDismissTooltip"));

        var next = GettingStarted.NextStep(steps);
        var signature = string.Join("|", steps.Select(step => step.Id + ":" + step.Done)) + "|" + next;
        if (string.Equals(signature, gettingStartedSignature, StringComparison.Ordinal)) return;
        gettingStartedSignature = signature;
        GettingStartedSteps.Children.Clear();
        foreach (var step in steps)
        {
            GettingStartedSteps.Children.Add(GettingStartedRow(step, string.Equals(step.Id, next, StringComparison.Ordinal)));
        }
    }

    private Grid GettingStartedRow(GettingStartedStepState step, bool isNext)
    {
        var key = "DashboardGettingStarted" + step.Id switch
        {
            GettingStarted.StepCreateSite => "CreateSite",
            GettingStarted.StepOpenSite => "OpenSite",
            GettingStarted.StepSendMail => "SendMail",
            _ => "TryDump"
        };
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon
        {
            Glyph = step.Done ? "\uE73E" : "\uEA3A",
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Style = StatusStyles.Glyph(step.Done ? StatusTone.Success : StatusTone.Neutral)
        };
        row.Children.Add(icon);
        var title = new TextBlock
        {
            Text = AppLocalization.Get(key + "Title"),
            FontWeight = isNext ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap
        };
        var description = new TextBlock
        {
            Text = AppLocalization.Get(key + "Description"),
            TextWrapping = TextWrapping.Wrap,
            Style = TextStyle("CaptionTextStyle")
        };
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        texts.Children.Add(description);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        var state = AppLocalization.Get(step.Done ? "DashboardGettingStartedDone" : "DashboardGettingStartedOpen");
        AutomationProperties.SetName(row, title.Text + ", " + state);
        if (!step.Done)
        {
            var action = new Button
            {
                Content = AppLocalization.Get(key + "Action"),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (isNext) action.Style = TextStyle("AccentButtonStyle");
            AutomationProperties.SetName(action, AppLocalization.Get(key + "Action") + ": " + title.Text);
            var id = step.Id;
            action.Click += (_, _) => RunGettingStartedStep(id);
            Grid.SetColumn(action, 2);
            row.Children.Add(action);
        }
        return row;
    }

    private void RunGettingStartedStep(string step)
    {
        switch (step)
        {
            case GettingStarted.StepCreateSite:
                App.MainWindow.StartSitesNextStep(OnboardingNextStep.CreateLaravel);
                break;
            case GettingStarted.StepOpenSite:
                OpenFirstSite();
                break;
            case GettingStarted.StepSendMail:
                App.MainWindow.NavigateToPage("mail");
                break;
            case GettingStarted.StepTryDump:
                App.MainWindow.NavigateToPage("dumps");
                break;
        }
    }

    // Opens the first site when the environment runs; otherwise Sites, where it can be started.
    private void OpenFirstSite()
    {
        if (!environment.IsRunning || gettingStartedSites.Count == 0)
        {
            App.MainWindow.NavigateToPage("sites");
            return;
        }
        var site = gettingStartedSites[0];
        try
        {
            var uri = SitePresentation.SiteUri(site, environment.IsRunning, environment.HttpPort, environment.HttpsPort);
            using var browser = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            App.MainWindow.RememberOpenedSite(site.Path);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            App.MainWindow.ShowToast(error.Message);
        }
    }

    private async void GettingStartedDismiss_Click(object sender, RoutedEventArgs e)
    {
        GettingStartedCard.Visibility = Visibility.Collapsed;
        if (!await SetGettingStartedDismissedAsync(true))
        {
            GettingStartedCard.Visibility = Visibility.Visible;
            return;
        }
        App.MainWindow.ShowToast(
            AppLocalization.Get("DashboardGettingStartedHidden"),
            AppLocalization.Get("CommonUndo"),
            async () =>
            {
                if (await SetGettingStartedDismissedAsync(false)) await ReloadGettingStartedAsync();
            }
        );
    }

    private async Task<bool> SetGettingStartedDismissedAsync(bool dismissed)
    {
        try
        {
            await Task.Run(() => settingsStore.UpdateGettingStartedDismissed(dismissed));
            return true;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            App.MainWindow.ShowToast(error.Message);
            return false;
        }
    }

    private async void MainWindow_GettingStartedChanged(object? sender, EventArgs e)
    {
        await ReloadGettingStartedAsync();
    }

    private async Task ReloadGettingStartedAsync()
    {
        try
        {
            var settings = await Task.Run(settingsStore.Load);
            if (XamlRoot is null) return;
            UpdateGettingStarted(settings, gettingStartedSites, gettingStartedMail, gettingStartedDumps);
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.Text.Json.JsonException)
        {
            // The next refresh shows the card again.
        }
    }
}
