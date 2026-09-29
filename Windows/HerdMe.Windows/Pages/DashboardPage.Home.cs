using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

// The calm Herd-style Overview: "Active services" (web server, PHP, mail, dumps and every
// running managed service, each with a dot and a way to jump to it), Open buttons on the side
// and the global PHP version picker. Counts, environment and recent items live on Activity.
public sealed partial class DashboardPage
{
    private bool loadingGlobalPhp;
    private string globalPhpCycle = string.Empty;

    private void ApplyHomeLayout(bool compact)
    {
        HomeSideColumn.Width = compact ? new GridLength(0) : new GridLength(260);
        Grid.SetRow(HomeSidePanel, compact ? 1 : 0);
        Grid.SetColumn(HomeSidePanel, compact ? 0 : 1);
        DashboardHomeGrid.ColumnSpacing = compact ? 0 : 32;
    }

    private void RenderActiveServices(IReadOnlyList<ManagedServiceInstance> instances, string defaultPhpCycle)
    {
        ActiveServicesList.Children.Clear();
        var index = 0;
        if (environment.IsRunning || environment.IsDegraded)
        {
            var ports = environment.HttpsPort is { } https
                ? AppLocalization.Format("DashboardActiveWebPorts", environment.HttpPort ?? 80, https)
                : string.Empty;
            AddActiveRow(
                ref index,
                AppLocalization.Get("DashboardActiveWebServer"),
                ports,
                environment.IsDegraded ? StatusTone.Caution : StatusTone.Success,
                "sites"
            );
            if (!string.IsNullOrWhiteSpace(defaultPhpCycle))
            {
                AddActiveRow(
                    ref index,
                    AppLocalization.Format("DashboardActivePhp", defaultPhpCycle),
                    AppLocalization.Get("DashboardActivePhpDetail"),
                    StatusTone.Success,
                    "php"
                );
            }
        }
        if (mailCapture.IsRunning)
        {
            AddActiveRow(
                ref index,
                AppLocalization.Get("DashboardActiveMail"),
                mailCapture.Port is { } mailPort ? AppLocalization.Format("DashboardActivePort", mailPort) : string.Empty,
                StatusTone.Success,
                "mail"
            );
        }
        if (dumpCapture.IsRunning)
        {
            AddActiveRow(
                ref index,
                AppLocalization.Get("DashboardActiveDumps"),
                dumpCapture.Port is { } dumpPort ? AppLocalization.Format("DashboardActivePort", dumpPort) : string.Empty,
                StatusTone.Success,
                "dumps"
            );
        }
        foreach (var instance in instances
            .Where(item => serviceManager.State(item.Id, item.DefinitionId) == ManagedServiceState.Running)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            AddActiveRow(
                ref index,
                instance.Name,
                AppLocalization.Format("DashboardActivePort", instance.Port),
                StatusTone.Success,
                "services"
            );
        }
        ActiveServicesEmptyText.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddActiveRow(ref int index, string name, string detail, StatusTone tone, string page)
    {
        var row = new Grid
        {
            Style = (Style)Application.Current.Resources[index % 2 == 0 ? "DetailRowAltStyle" : "DetailRowStyle"]
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Style = StatusStyles.Dot(tone), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(dot);

        var label = new TextBlock
        {
            Text = name,
            Style = (Style)Application.Current.Resources["DetailRowLabelStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        if (detail.Length > 0)
        {
            var caption = new TextBlock
            {
                Text = detail,
                Style = (Style)Application.Current.Resources["CaptionTextStyle"],
                VerticalAlignment = VerticalAlignment.Center,
                FlowDirection = FlowDirection.LeftToRight
            };
            Grid.SetColumn(caption, 2);
            row.Children.Add(caption);
        }

        var open = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Content = new FontIcon { Glyph = "\uE8A7", FontSize = 14 },
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        var openName = AppLocalization.Format("DashboardActiveOpen", name);
        AutomationProperties.SetName(open, openName);
        ToolTipService.SetToolTip(open, openName);
        open.Click += (_, _) => App.MainWindow.NavigateToPage(page);
        Grid.SetColumn(open, 3);
        row.Children.Add(open);

        ActiveServicesList.Children.Add(row);
        index++;
    }

    private async Task RenderGlobalPhpAsync(string defaultPhpCycle, CancellationToken cancellationToken)
    {
        var cycles = await Task.Run(phpInstaller.InstalledCycles, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        loadingGlobalPhp = true;
        try
        {
            globalPhpCycle = defaultPhpCycle;
            GlobalPhpBox.Items.Clear();
            foreach (var cycle in cycles.OrderByDescending(ParseCycle))
            {
                GlobalPhpBox.Items.Add(new ComboBoxItem
                {
                    Content = AppLocalization.Format("DashboardGlobalPhpItem", cycle),
                    Tag = cycle
                });
            }
            GlobalPhpBox.SelectedItem = GlobalPhpBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, defaultPhpCycle, StringComparison.Ordinal));
            GlobalPhpBox.IsEnabled = GlobalPhpBox.Items.Count > 1;
            GlobalPhpHint.Text = GlobalPhpBox.Items.Count == 0
                ? AppLocalization.Get("DashboardGlobalPhpNone")
                : string.Empty;
            GlobalPhpHint.Visibility = GlobalPhpHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            loadingGlobalPhp = false;
        }
    }

    private static Version ParseCycle(string cycle) =>
        Version.TryParse(cycle, out var version) ? version : new Version(0, 0);

    private void GlobalPhpBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingGlobalPhp) return;
        if (GlobalPhpBox.SelectedItem is not ComboBoxItem { Tag: string cycle }) return;
        if (string.Equals(cycle, globalPhpCycle, StringComparison.Ordinal)) return;
        globalPhpCycle = cycle;
        ((App)Application.Current).SwitchDefaultPhp(cycle);
    }
}
