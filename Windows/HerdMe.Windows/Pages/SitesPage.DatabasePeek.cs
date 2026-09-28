using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

// Read-only table peek for the Sites Database tab: pick a table, see its first rows. Nothing here
// can change data: DatabasePeek only runs SELECTs inside a READ ONLY transaction.
public sealed partial class SitesPage
{
    private async void BrowseDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSite is not { } site) return;
        if (!TryCurrentSiteDatabase(site, out var instance, out var provisioning, out var error))
        {
            await ShowErrorAsync(error);
            return;
        }

        var tableBox = new ComboBox
        {
            Header = AppLocalization.Get("SitesDatabasePeekTableField"),
            PlaceholderText = AppLocalization.Get("SitesDatabasePeekLoadingTables"),
            DisplayMemberPath = nameof(DatabasePeekTable.DisplayName),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false
        };
        var statusText = new TextBlock
        {
            Text = AppLocalization.Get("SitesDatabasePeekLoadingTables"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
        };
        var outputBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Width = 640,
            Height = 340
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(outputBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(outputBox, ScrollBarVisibility.Auto);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            outputBox, AppLocalization.Get("SitesDatabasePeekRowsName")
        );
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(tableBox);
        content.Children.Add(statusText);
        content.Children.Add(outputBox);
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Format("SitesDatabasePeekTitle", provisioning.DatabaseName),
            Content = content,
            SecondaryButtonText = AppLocalization.Get("SitesDatabasePeekCopy"),
            CloseButtonText = AppLocalization.Get("SitesDone"),
            DefaultButton = ContentDialogButton.Close
        };
        dialog.IsSecondaryButtonEnabled = false;

        using var closed = new CancellationTokenSource();
        CancellationTokenSource? rowsRequest = null;
        dialog.Closed += (_, _) =>
        {
            closed.Cancel();
            rowsRequest?.Cancel();
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            // Copy keeps the dialog open.
            args.Cancel = true;
            if (outputBox.Text.Length > 0) CopyText(outputBox.Text);
        };
        tableBox.SelectionChanged += async (_, _) =>
        {
            if (tableBox.SelectedItem is not DatabasePeekTable table) return;
            rowsRequest?.Cancel();
            var request = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
            rowsRequest = request;
            request.CancelAfter(TimeSpan.FromSeconds(10));
            statusText.Text = AppLocalization.Format("SitesDatabasePeekLoadingRows", table.DisplayName);
            outputBox.Text = string.Empty;
            dialog.IsSecondaryButtonEnabled = false;
            try
            {
                var rows = await serviceManager.PeekSiteDatabaseRowsAsync(instance, provisioning, table, request.Token);
                if (request.IsCancellationRequested) return;
                if (rows.Columns.Count == 0 || rows.Rows.Count == 0)
                {
                    statusText.Text = AppLocalization.Format("SitesDatabasePeekEmpty", table.DisplayName);
                    return;
                }
                outputBox.Text = TinkerOutputFormatter.TextTable(rows.Columns, rows.Rows);
                statusText.Text = AppLocalization.Format(
                    rows.Rows.Count >= DatabasePeek.RowLimit ? "SitesDatabasePeekRowsLimited" : "SitesDatabasePeekRows",
                    rows.Rows.Count,
                    table.DisplayName
                );
                dialog.IsSecondaryButtonEnabled = true;
            }
            catch (OperationCanceledException)
            {
                if (!closed.IsCancellationRequested && ReferenceEquals(rowsRequest, request))
                {
                    statusText.Text = AppLocalization.Get("SitesDatabasePeekTimedOut");
                }
            }
            catch (Exception peekError) when (peekError is IOException or InvalidOperationException
                or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                if (ReferenceEquals(rowsRequest, request)) statusText.Text = peekError.Message;
            }
        };

        var showing = dialog.ShowAsync();
        try
        {
            using var tablesRequest = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
            tablesRequest.CancelAfter(TimeSpan.FromSeconds(10));
            var tables = await serviceManager.PeekSiteDatabaseTablesAsync(instance, provisioning, tablesRequest.Token);
            if (!closed.IsCancellationRequested)
            {
                tableBox.ItemsSource = tables;
                tableBox.PlaceholderText = AppLocalization.Get("SitesDatabasePeekPickTable");
                tableBox.IsEnabled = tables.Count > 0;
                statusText.Text = tables.Count == 0
                    ? AppLocalization.Get("SitesDatabasePeekNoTables")
                    : AppLocalization.Format(
                        tables.Count >= DatabasePeek.MaximumTables ? "SitesDatabasePeekTablesLimited" : "SitesDatabasePeekTables",
                        tables.Count
                    );
                if (tables.Count > 0) tableBox.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
            if (!closed.IsCancellationRequested) statusText.Text = AppLocalization.Get("SitesDatabasePeekTimedOut");
        }
        catch (Exception peekError) when (peekError is IOException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            if (!closed.IsCancellationRequested) statusText.Text = peekError.Message;
        }
        await showing;
        rowsRequest?.Dispose();
    }
}
