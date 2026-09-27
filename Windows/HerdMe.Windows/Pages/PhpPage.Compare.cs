using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace HerdMe.Windows.Pages;

// "Compare PHP versions": every installed version in one table (version number, then which
// extensions it loads), so picking a version for a project does not mean flipping the
// selector back and forth. It only reads; nothing is changed from here.
public sealed partial class PhpPage
{
    private IReadOnlyList<PhpVersionColumn> compareColumns = [];
    private bool comparing;

    private sealed record PhpVersionColumn(
        string Cycle,
        string? Version,
        IReadOnlyDictionary<string, PhpExtensionState> Extensions,
        string? Error
    );

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (comparing) return;
        comparing = true;
        CompareButton.IsEnabled = false;
        CompareStatusText.Text = AppLocalization.Get("PhpCompareRunning");
        try
        {
            var columns = new List<PhpVersionColumn>();
            foreach (var cycle in runtimeInstaller.InstalledCycles())
            {
                if (!loaded) return;
                var version = runtimeInstaller.InstalledVersion(cycle);
                try
                {
                    var states = await extensionManager.InspectAsync(cycle);
                    columns.Add(new PhpVersionColumn(
                        cycle,
                        version,
                        states.GroupBy(state => state.Name, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase),
                        null
                    ));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    columns.Add(new PhpVersionColumn(
                        cycle,
                        version,
                        new Dictionary<string, PhpExtensionState>(StringComparer.OrdinalIgnoreCase),
                        error.Message
                    ));
                }
            }
            compareColumns = columns;
            RenderComparison();
            CompareStatusText.Text = columns.Count switch
            {
                0 => AppLocalization.Get("PhpCompareNothingInstalled"),
                1 => AppLocalization.Get("PhpCompareOnlyOne"),
                _ => AppLocalization.Format("PhpCompareDone", columns.Count)
            };
        }
        finally
        {
            comparing = false;
            CompareButton.IsEnabled = true;
        }
    }

    private void CompareDifferencesOnly_Click(object sender, RoutedEventArgs e) => RenderComparison();

    // A cell's state, for "only differences" and for the screen reader name.
    private static string CompareState(PhpExtensionState? state) => state switch
    {
        null => "PhpCompareAbsent",
        { Loaded: true } => "PhpExtensionLoaded",
        { Enabled: true } => "PhpExtensionEnabled",
        _ => "PhpExtensionDisabled"
    };

    private void RenderComparison()
    {
        CompareGrid.Children.Clear();
        CompareGrid.RowDefinitions.Clear();
        CompareGrid.ColumnDefinitions.Clear();
        var columns = compareColumns;
        if (columns.Count == 0) return;

        CompareGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var index = 0; index < columns.Count; index++)
        {
            CompareGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var row = 0;
        AddCompareRow(ref row);
        AddCompareText(row, 0, AppLocalization.Get("PhpCompareExtensionHeader"), header: true);
        for (var index = 0; index < columns.Count; index++)
        {
            AddCompareText(row, index + 1, $"PHP {columns[index].Cycle}", header: true);
        }

        AddCompareRow(ref row);
        AddCompareText(row, 0, AppLocalization.Get("PhpCompareVersionRow"), header: false);
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            AddCompareText(
                row,
                index + 1,
                column.Error is null
                    ? column.Version ?? AppLocalization.Get("CommonNotInstalled")
                    : AppLocalization.Format("PhpCompareFailed", column.Error),
                header: false
            );
        }

        var names = columns.SelectMany(column => column.Extensions.Values)
            .GroupBy(state => state.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.First().Name, Required: group.Any(state => state.Required)))
            .OrderByDescending(item => item.Required)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var differencesOnly = CompareDifferencesOnly.IsChecked == true;
        var shown = 0;
        foreach (var (name, required) in names)
        {
            var states = columns.Select(column =>
                column.Extensions.TryGetValue(name, out var state) ? state : null).ToList();
            if (differencesOnly && states.Select(CompareState).Distinct(StringComparer.Ordinal).Count() < 2) continue;
            AddCompareRow(ref row);
            AddCompareText(
                row,
                0,
                required ? AppLocalization.Format("PhpCompareRequiredName", name) : name,
                header: false
            );
            for (var index = 0; index < states.Count; index++)
            {
                AddCompareCell(row, index + 1, name, columns[index].Cycle, states[index]);
            }
            shown++;
        }
        if (differencesOnly && shown == 0)
        {
            AddCompareRow(ref row);
            var same = AddCompareText(row, 0, AppLocalization.Get("PhpCompareNoDifferences"), header: false);
            Grid.SetColumnSpan(same, columns.Count + 1);
        }
    }

    private void AddCompareRow(ref int row)
    {
        CompareGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row = CompareGrid.RowDefinitions.Count - 1;
    }

    private TextBlock AddCompareText(int row, int column, string text, bool header)
    {
        var block = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap
        };
        if (header)
        {
            block.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            AutomationProperties.SetHeadingLevel(block, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3);
        }
        Grid.SetRow(block, row);
        Grid.SetColumn(block, column);
        CompareGrid.Children.Add(block);
        return block;
    }

    private void AddCompareCell(int row, int column, string name, string cycle, PhpExtensionState? state)
    {
        var key = CompareState(state);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (state is { Loaded: true })
        {
            panel.Children.Add(new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"]
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = state is null ? "\u2014" : AppLocalization.Get(key),
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            VerticalAlignment = VerticalAlignment.Center
        });
        AutomationProperties.SetName(panel, AppLocalization.Format("PhpCompareCellName", name, cycle, AppLocalization.Get(key)));
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
        CompareGrid.Children.Add(panel);
    }
}
