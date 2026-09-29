using System.Collections.ObjectModel;
using System.Diagnostics;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

// One section of the Services list (Database, Cache & Queue, Search, Storage).
public sealed class ServiceGroup : ObservableCollection<ManagedServiceRow>
{
    public ServiceGroup(ServiceGroupKind kind, string title)
    {
        Kind = kind;
        Title = title;
    }

    public ServiceGroupKind Kind { get; }

    public string Title { get; }
}

// Services list + details: services are grouped by kind in a list, and the selected one shows
// its documentation link, its Laravel environment variables (secrets masked, copy gives the real
// values) and the end of its log in a pane beside the list (below it on narrow windows).
public sealed partial class ServicesPage
{
    private Guid? selectedServiceId;
    private bool syncingSelection;
    private string? detailEnvironmentKey;
    private Guid? detailLogFor;

    public ObservableCollection<ServiceGroup> Groups { get; } = [];

    private ManagedServiceRow? SelectedRow() =>
        selectedServiceId is { } id ? Rows.FirstOrDefault(row => row.Id == id) : null;

    // Keeps the grouped list in step with Rows. Unchanged rows keep their containers (and any
    // open menu); only sections whose members changed are refilled.
    private void SyncGroups()
    {
        var wanted = ServiceDirectory.Group(Rows, row => row.DefinitionId, row => row.Name);
        syncingSelection = true;
        try
        {
            if (!wanted.Select(group => group.Kind).SequenceEqual(Groups.Select(group => group.Kind)))
            {
                Groups.Clear();
                foreach (var (kind, items) in wanted)
                {
                    var group = new ServiceGroup(kind, GroupTitle(kind));
                    foreach (var item in items) group.Add(item);
                    Groups.Add(group);
                }
            }
            else
            {
                for (var index = 0; index < wanted.Count; index++)
                {
                    var group = Groups[index];
                    var items = wanted[index].Items;
                    if (group.Count == items.Count
                        && group.Select(row => row.Id).SequenceEqual(items.Select(row => row.Id)))
                    {
                        for (var position = 0; position < items.Count; position++)
                        {
                            if (!ReferenceEquals(group[position], items[position])) group[position] = items[position];
                        }
                        continue;
                    }
                    group.Clear();
                    foreach (var item in items) group.Add(item);
                }
            }

            // Keep the selected service selected; otherwise pick the first one so the details
            // pane is never empty while there are services.
            var target = SelectedRow() ?? Groups.FirstOrDefault()?.FirstOrDefault();
            selectedServiceId = target?.Id;
            if (!ReferenceEquals(ServiceList.SelectedItem, target)) ServiceList.SelectedItem = target;
        }
        finally
        {
            syncingSelection = false;
        }
        ServiceDetailPane.Visibility = Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowDetails(SelectedRow());
    }

    private static string GroupTitle(ServiceGroupKind kind) =>
        AppLocalization.Get(ServiceDirectory.GroupTitleKey(kind));

    private void ServiceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingSelection) return;
        var row = ServiceList.SelectedItem as ManagedServiceRow;
        selectedServiceId = row?.Id;
        ShowDetails(row);
        RequestLogTails();
    }

    private void ShowDetails(ManagedServiceRow? row)
    {
        ServiceDetailContent.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        DetailPlaceholder.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        if (row is null)
        {
            detailEnvironmentKey = null;
            detailLogFor = null;
            return;
        }

        DetailNameText.Text = row.Name;
        DetailSummaryText.Text = row.State == ManagedServiceState.NotInstalled
            ? row.Summary
            : AppLocalization.Format("ServicesDetailSummary", row.Status, row.Summary);
        DetailRunningDot.Visibility = row.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        DetailStoppedDot.Visibility = row.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ServiceDetailPane, row.CardName);

        DetailAddressRow.Visibility = row.CanCopyAddress ? Visibility.Visible : Visibility.Collapsed;
        DetailAddressText.Text = row.Address;
        ToolTipService.SetToolTip(DetailAddressText, row.Address);
        SetTaggedButton(DetailCopyAddressButton, row.Id, row.CopyAddressLabel);

        var documentation = ServiceDirectory.DocumentationUri(row.DefinitionId);
        DetailDocumentationText.Text = documentation?.Host ?? AppLocalization.Get("ServicesDetailNoDocumentation");
        DetailDocumentationButton.Tag = row.Id;
        DetailDocumentationButton.IsEnabled = documentation is not null;
        ToolTipService.SetToolTip(DetailDocumentationButton, documentation?.AbsoluteUri);

        SetTaggedButton(
            DetailCopyEnvironmentButton,
            row.Id,
            AppLocalization.Format("ServicesDetailCopyEnvironment", row.Name)
        );
        DetailOpenLogButton.Tag = row.Id;
        ToolTipService.SetToolTip(DetailOpenLogButton, manager.LogPath(row.Id));

        // Credentials are read only when another service (or a changed port) is shown.
        var environmentKey = $"{row.Id:N}|{row.DefinitionId}|{row.Port}";
        if (environmentKey != detailEnvironmentKey)
        {
            detailEnvironmentKey = environmentKey;
            LoadDetailEnvironment(row);
        }
        if (detailLogFor != row.Id)
        {
            detailLogFor = row.Id;
            DetailLogText.Text = string.Empty;
        }
    }

    private static void SetTaggedButton(Button button, Guid id, string label)
    {
        button.Tag = id;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
    }

    private void LoadDetailEnvironment(ManagedServiceRow row)
    {
        try
        {
            var instance = manager.LoadInstances().FirstOrDefault(candidate => candidate.Id == row.Id);
            IReadOnlyList<ServiceEnvironmentVariable> variables = instance is null
                ? []
                : manager.EnvironmentVariables(instance);
            DetailEnvironmentText.Text = variables.Count == 0
                ? AppLocalization.Get("ServicesDetailNoEnvironment")
                : ServiceDirectory.MaskedEnvironment(variables).TrimEnd('\n');
            DetailCopyEnvironmentButton.IsEnabled = variables.Count > 0;
        }
        catch (Exception error)
        {
            DetailEnvironmentText.Text = error.Message;
            DetailCopyEnvironmentButton.IsEnabled = false;
        }
    }

    private void ShowDetailLog(string text)
    {
        var next = text.Length == 0 ? AppLocalization.Get("ServicesDetailNoLog") : text;
        if (string.Equals(DetailLogText.Text, next, StringComparison.Ordinal)) return;
        // Follow the end of the log unless the reader scrolled up to look at something.
        var following = DetailLogScroller.ScrollableHeight - DetailLogScroller.VerticalOffset < 4;
        DetailLogText.Text = next;
        if (!following) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            DetailLogScroller.UpdateLayout();
            DetailLogScroller.ChangeView(null, DetailLogScroller.ScrollableHeight, null, true);
        });
    }

    private async void OpenDocumentation_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        if (ServiceDirectory.DocumentationUri(instance.DefinitionId) is not { } documentation) return;
        try
        {
            // The browser is one of the documented shell-execute exceptions.
            Process.Start(new ProcessStartInfo(documentation.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    // Opens the service log in the default text editor through Explorer.
    private async void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance)) return;
        var path = manager.LogPath(instance.Id);
        if (!File.Exists(path))
        {
            await ShowErrorAsync(AppLocalization.Format("ServicesDetailLogMissing", instance.Name));
            return;
        }
        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await ShowErrorAsync(error.Message);
        }
    }

    private void ToggleAddService_Click(object sender, RoutedEventArgs e) =>
        ShowAddService(AddServiceCard.Visibility != Visibility.Visible, focus: true);

    private void CancelAddService_Click(object sender, RoutedEventArgs e)
    {
        ShowAddService(false);
        AddServiceToggleButton.Focus(FocusState.Programmatic);
    }

    private void ShowAddService(bool show, bool focus = false)
    {
        AddServiceCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && focus) ServiceTypeBox.Focus(FocusState.Programmatic);
    }

    // Wide windows: list and details side by side. Narrow: details below the list.
    private void ApplyDetailLayout(bool stacked)
    {
        ServiceDetailColumn.Width = stacked ? new GridLength(0) : new GridLength(400);
        ServiceDetailRow.Height = stacked ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        ServicesContent.ColumnSpacing = stacked ? 0 : 16;
        ServicesContent.RowSpacing = stacked ? 16 : 0;
        Grid.SetRow(ServiceDetailPane, stacked ? 1 : 0);
        Grid.SetColumn(ServiceDetailPane, stacked ? 0 : 1);
    }
}
