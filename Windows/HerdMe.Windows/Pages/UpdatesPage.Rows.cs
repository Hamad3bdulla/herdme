using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

// Component rows: grouped by kind, each with from -> to, badges (major, security, pinned), what's
// new, its own progress, Cancel / Retry, and a "..." menu (skip, remind me in a week, stay on a
// major line, roll back). Skipped, snoozed and pinned-away updates wait in their own group.
public sealed partial class UpdatesPage
{
    private sealed record RowControls(
        ManagedComponentUpdate Update,
        TextBlock State,
        ProgressBar Progress,
        TextBlock Error,
        Button Action,
        FontIcon Done,
        Button More
    );

    private readonly Dictionary<string, RowControls> rowControls = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Tag, string Label, string Glyph)[] Groups =
    [
        ("php", "UpdatesGroupPhp", "\uE943"),
        ("node", "UpdatesGroupNode", "\uE8F1"),
        ("services", "UpdatesGroupServices", "\uE71D"),
        ("debugger", "UpdatesGroupDebugger", "\uE90F"),
        ("tools", "UpdatesGroupTools", "\uE756")
    ];

    private static int GroupIndex(string pageTag) => pageTag switch
    {
        "php" => 0,
        "node" => 1,
        "services" => 2,
        "debugger" => 3,
        _ => 4
    };

    private static string GroupGlyph(string pageTag) => Groups[GroupIndex(pageTag)].Glyph;

    private void RenderComponents()
    {
        ComponentRows.Children.Clear();
        rowControls.Clear();
        var now = DateTimeOffset.UtcNow;
        var prefs = preferences.Load();
        var offered = Offered(prefs, now);
        var visible = offered
            .Concat(finished.Where(item => !offered.Any(update => SameId(update, item))))
            .ToList();
        foreach (var group in visible.GroupBy(update => GroupIndex(update.PageTag)).OrderBy(group => group.Key))
        {
            var rows = group.Select(update => UpdateRow(update, prefs)).ToList();
            ComponentRows.Children.Add(GroupPanel(AppLocalization.Get(Groups[group.Key].Label), rows));
        }
        var hidden = updates
            .Where(update => !UpdatePreferencesStore.IsOffered(update, prefs, now) && !IsFinished(update))
            .ToList();
        if (hidden.Count > 0)
        {
            ComponentRows.Children.Add(GroupPanel(
                AppLocalization.Get("UpdatesHiddenGroup"),
                hidden.Select(update => HiddenRow(update, prefs, now)).ToList()
            ));
        }
        EmptyState.Visibility = visible.Count == 0
            && checkedAt is not null
            && ComponentCheckProgress.Visibility == Visibility.Collapsed
                ? Visibility.Visible
                : Visibility.Collapsed;
        RefreshRowStates();
    }

    private static StackPanel GroupPanel(string label, IReadOnlyList<UIElement> rows)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Style = S("QuietSectionLabelStyle") });
        var list = new StackPanel();
        for (var index = 0; index < rows.Count; index++)
        {
            if (index > 0) list.Children.Add(Divider());
            list.Children.Add(rows[index]);
        }
        panel.Children.Add(new Border { Style = S("SettingsGroupStyle"), Child = list });
        return panel;
    }

    private Grid UpdateRow(ManagedComponentUpdate update, UpdatePreferences prefs)
    {
        var grid = RowGrid(GroupGlyph(update.PageTag));
        AutomationProperties.SetName(grid, update.Name);
        AutomationProperties.SetHelpText(grid, ComponentCategory(update));

        var text = new StackPanel { Style = S("SettingsRowTextStyle") };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock
        {
            Text = update.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Style = S("SettingsRowTitleStyle")
        });
        if (IsMajor(update)) title.Children.Add(Pill(AppLocalization.Get("UpdatesBadgeMajor"), "Caution"));
        if (update.Security) title.Children.Add(Pill(AppLocalization.Get("UpdatesBadgeSecurity"), "Critical"));
        if (prefs.PinnedLines.TryGetValue(update.Id, out var line))
            title.Children.Add(Pill(AppLocalization.Format("UpdatesBadgePinned", line), "Neutral"));
        text.Children.Add(title);

        var version = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var change = AppLocalization.Format("UpdatesVersionChange", update.InstalledVersion, update.LatestVersion);
        var size = ComponentUpdateRunner.ApproximateDownloadBytes(update.Id);
        version.Children.Add(new TextBlock
        {
            Text = size > 0 ? change + "  \u00B7  " + MegabytesText(size) : change,
            VerticalAlignment = VerticalAlignment.Center,
            Style = S("SettingsRowDescriptionStyle")
        });
        if (ComponentUpdateRunner.ReleaseNotesUri(update.Id, update.LatestVersion) is { } notes)
        {
            version.Children.Add(new HyperlinkButton
            {
                Content = AppLocalization.Get("UpdatesWhatsNew"),
                NavigateUri = notes,
                Padding = new Thickness(6, 0, 6, 0),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        text.Children.Add(version);

        var state = new TextBlock { Style = S("SettingsRowDescriptionStyle"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var progress = new ProgressBar
        {
            Margin = new Thickness(0, 6, 0, 0),
            Width = 360,
            MaxWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed
        };
        var error = new TextBlock { Style = S("StatusCriticalTextStyle"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        text.Children.Add(state);
        text.Children.Add(progress);
        text.Children.Add(error);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var done = new FontIcon
        {
            Glyph = "\uE930",
            Style = S("StatusGlyphSuccessStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(done, AppLocalization.Format("UpdatesUpdatedTo", update.LatestVersion));
        var action = new Button { VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(action, "UpdatesRow_" + update.Id.Replace(':', '_'));
        action.Click += async (_, _) => await RowActionAsync(update);
        var more = MoreButton(RowMenu(update, prefs));
        actions.Children.Add(done);
        actions.Children.Add(action);
        actions.Children.Add(more);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        rowControls[update.Id] = new RowControls(update, state, progress, error, action, done, more);
        return grid;
    }

    private Grid HiddenRow(ManagedComponentUpdate update, UpdatePreferences prefs, DateTimeOffset now)
    {
        var grid = RowGrid(GroupGlyph(update.PageTag));
        var text = new StackPanel { Style = S("SettingsRowTextStyle") };
        text.Children.Add(new TextBlock { Text = update.Name, Style = S("SettingsRowTitleStyle") });
        var pinned = UpdatePreferencesStore.IsHeldByPin(update, prefs);
        string reason;
        if (pinned)
            reason = AppLocalization.Format("UpdatesHiddenPinned", prefs.PinnedLines[update.Id], update.LatestVersion);
        else if (prefs.SnoozedUntil.TryGetValue(update.Id, out var until) && until > now)
            reason = AppLocalization.Format("UpdatesHiddenSnoozed", update.LatestVersion, until.ToLocalTime().ToString("d"));
        else
            reason = AppLocalization.Format("UpdatesHiddenSkipped", update.LatestVersion);
        text.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, Style = S("SettingsRowDescriptionStyle") });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var button = new Button { Content = AppLocalization.Get("UpdatesShowAgain"), VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) =>
        {
            if (pinned) preferences.Unpin(update.Id);
            else preferences.Unhide(update.Id);
        };
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        return grid;
    }

    private static Grid RowGrid(string glyph)
    {
        var grid = new Grid { Style = S("SettingsRowStyle") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = glyph, Style = S("SettingsRowIconStyle") });
        return grid;
    }

    private static Button MoreButton(MenuFlyout menu)
    {
        var button = new Button
        {
            Style = S("ToolbarIconButtonStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "\uE712", FontSize = 16 },
            Flyout = menu,
            Visibility = menu.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed
        };
        var label = AppLocalization.Get("UpdatesMoreOptions");
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += async (_, _) => await action();
        return item;
    }

    private MenuFlyout RowMenu(ManagedComponentUpdate update, UpdatePreferences prefs)
    {
        var menu = new MenuFlyout();
        if (ComponentUpdateRunner.ReleaseNotesUri(update.Id, update.LatestVersion) is { } notes)
            menu.Items.Add(MenuItem(AppLocalization.Get("UpdatesWhatsNew"), "\uE8A5", () => OpenAsync(notes)));
        if (IsFinished(update))
        {
            AddRollBack(menu, update.Id, update.Name);
            return menu;
        }
        menu.Items.Add(MenuItem(AppLocalization.Get("UpdatesSkipVersion"), "\uE8D8", () =>
        {
            preferences.Skip(update.Id, update.LatestVersion);
            Toast(AppLocalization.Format("UpdatesSkippedToast", update.Name, update.LatestVersion), update.Id, preferences);
            return Task.CompletedTask;
        }));
        menu.Items.Add(MenuItem(AppLocalization.Get("UpdatesRemindLater"), "\uE823", () =>
        {
            preferences.Snooze(update.Id, DateTimeOffset.UtcNow);
            Toast(AppLocalization.Format("UpdatesSnoozedToast", update.Name), update.Id, preferences);
            return Task.CompletedTask;
        }));
        AddPin(menu, update.Id, update.InstalledVersion, prefs);
        AddRollBack(menu, update.Id, update.Name);
        return menu;
    }

    // Stay on the installed major line: a newer major is held back until unpinned.
    private void AddPin(MenuFlyout menu, string id, string installedVersion, UpdatePreferences prefs)
    {
        if (!UpdatePreferencesStore.CanPin(id)) return;
        if (prefs.PinnedLines.TryGetValue(id, out var pinned))
        {
            menu.Items.Add(MenuItem(AppLocalization.Format("UpdatesUnpin", pinned), "\uE77A", () =>
            {
                preferences.Unpin(id);
                return Task.CompletedTask;
            }));
            return;
        }
        var line = UpdatePreferencesStore.MajorOf(installedVersion);
        if (line.Length == 0) return;
        menu.Items.Add(MenuItem(AppLocalization.Format("UpdatesPin", line), "\uE718", () =>
        {
            preferences.Pin(id, line);
            Toast(AppLocalization.Format("UpdatesPinnedToast", line));
            return Task.CompletedTask;
        }));
    }

    private void AddRollBack(MenuFlyout menu, string id, string name)
    {
        if (!runner.CanRollBack(id)) return;
        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem(AppLocalization.Get("UpdatesRollBack"), "\uE7A7", () => RollBackAsync(id, name)));
    }

    private async Task RowActionAsync(ManagedComponentUpdate update)
    {
        var key = ComponentUpdateRunner.OperationKey(update);
        if (string.Equals(runner.Active, key, StringComparison.OrdinalIgnoreCase))
        {
            RuntimeOperations.Shared.Cancel(key);
            return;
        }
        if (runner.IsQueued(key))
        {
            runner.Dequeue(key);
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var prefs = preferences.Load();
        // Composer and the Laravel installer update together.
        var together = updates
            .Where(item => ComponentUpdateRunner.OperationKey(item).Equals(key, StringComparison.OrdinalIgnoreCase)
                && UpdatePreferencesStore.IsOffered(item, prefs, now)
                && !IsFinished(item))
            .ToList();
        if (together.Count == 0) together.Add(update);
        await RunUpdatesAsync(together);
    }

    // Progress, queue, failure and done state of every row, in place.
    private void RefreshRowStates()
    {
        if (rowControls.Count == 0) return;
        var snapshot = RuntimeOperations.Shared.Snapshot();
        foreach (var controls in rowControls.Values) ApplyRowState(controls, snapshot);
    }

    private void ApplyRowState(RowControls controls, IReadOnlyList<RuntimeOperation> snapshot)
    {
        var update = controls.Update;
        var key = ComponentUpdateRunner.OperationKey(update);
        var done = IsFinished(update);
        var active = !done && string.Equals(runner.Active, key, StringComparison.OrdinalIgnoreCase);
        var queued = !done && !active && runner.IsQueued(key);
        var last = done || active || queued ? null : runner.LastResult(key);
        string? state = null;
        controls.Error.Visibility = Visibility.Collapsed;
        controls.Progress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (done)
        {
            state = AppLocalization.Format("UpdatesUpdatedTo", update.LatestVersion);
        }
        else if (active)
        {
            var operation = snapshot.FirstOrDefault(item => item.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (operation is { Progress.IsActive: true })
            {
                var row = ServiceDownloadRow.From(operation.Progress, update.Name);
                state = row.Detail;
                controls.Progress.IsIndeterminate = row.IsIndeterminate;
                controls.Progress.Value = row.Percentage;
            }
            else
            {
                state = AppLocalization.Get("UpdatesStagePreparing");
                controls.Progress.IsIndeterminate = true;
            }
        }
        else if (queued)
        {
            state = AppLocalization.Get("UpdatesStageQueued");
        }
        else if (last is { Outcome: UpdateOutcome.Cancelled })
        {
            state = AppLocalization.Get("UpdatesStageCancelled");
        }
        else if (last is { Outcome: UpdateOutcome.Failed or UpdateOutcome.Restored } failure)
        {
            controls.Error.Text = string.IsNullOrWhiteSpace(failure.Error)
                ? AppLocalization.Get("ErrorOperation")
                : failure.Error;
            controls.Error.Visibility = Visibility.Visible;
        }
        controls.State.Text = state ?? string.Empty;
        controls.State.Visibility = state is null ? Visibility.Collapsed : Visibility.Visible;

        controls.Done.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        controls.Action.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        controls.Action.IsEnabled = !busy;
        controls.More.IsEnabled = !active && !queued;
        if (active || queued)
        {
            SetButton(controls.Action, Symbol.Cancel, AppLocalization.Get("CommonCancel"));
        }
        else if (last is { Outcome: not UpdateOutcome.Updated })
        {
            SetButton(controls.Action, Symbol.Refresh, AppLocalization.Get("UpdatesRetry"));
        }
        else
        {
            SetButton(controls.Action, Symbol.Download, AppLocalization.Get("CommonUpdate"));
        }
    }

    private static void SetButton(Button button, Symbol symbol, string label)
    {
        if (button.Tag is string current && current == label) return;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new SymbolIcon(symbol));
        content.Children.Add(new TextBlock { Text = label });
        button.Content = content;
        button.Tag = label;
        AutomationProperties.SetName(button, label);
    }

    // Installed components with nothing new, with pinning, rollback and what's new.
    private async Task LoadInstalledAsync()
    {
        try
        {
            installed = await Task.Run(() => componentUpdates.InstalledComponentsAsync());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            installed = [];
            await DiagnosticLog.WriteFailureAsync(
                "updates",
                "installed-list-failed",
                "The Updates page could not list the installed components.",
                error.ToString()
            );
        }
        if (loaded) RenderUpToDate();
    }

    private void RenderUpToDate()
    {
        UpToDateRows.Children.Clear();
        var prefs = preferences.Load();
        var pending = updates
            .Where(update => !IsFinished(update))
            .Select(update => update.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = installed.Where(component => !pending.Contains(component.Id)).ToList();
        for (var index = 0; index < current.Count; index++)
        {
            if (index > 0) UpToDateRows.Children.Add(Divider());
            UpToDateRows.Children.Add(InstalledRow(current[index], prefs));
        }
        UpToDateHeader.Text = AppLocalization.Format("UpdatesUpToDateHeader", current.Count);
        UpToDateExpander.Visibility = current.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Grid InstalledRow(InstalledComponent component, UpdatePreferences prefs)
    {
        var grid = RowGrid(GroupGlyph(component.PageTag));
        var text = new StackPanel { Style = S("SettingsRowTextStyle") };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = component.Name, Style = S("SettingsRowTitleStyle") });
        if (prefs.PinnedLines.TryGetValue(component.Id, out var line))
            title.Children.Add(Pill(AppLocalization.Format("UpdatesBadgePinned", line), "Neutral"));
        text.Children.Add(title);
        text.Children.Add(new TextBlock
        {
            Text = AppLocalization.Format("UpdatesInstalledVersion", component.Version),
            Style = S("SettingsRowDescriptionStyle")
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var menu = new MenuFlyout();
        if (ComponentUpdateRunner.ReleaseNotesUri(component.Id, component.Version) is { } notes)
            menu.Items.Add(MenuItem(AppLocalization.Get("UpdatesWhatsNew"), "\uE8A5", () => OpenAsync(notes)));
        AddPin(menu, component.Id, component.Version, prefs);
        AddRollBack(menu, component.Id, component.Name);
        var more = MoreButton(menu);
        Grid.SetColumn(more, 2);
        grid.Children.Add(more);
        return grid;
    }

    // Puts the version from before the last update back (kept for seven days).
    private async Task RollBackAsync(string id, string name)
    {
        if (busy || runner.Rollbacks.Find(id, DateTimeOffset.UtcNow) is not { } point) return;
        var content = new StackPanel { Spacing = 12, MinWidth = 360 };
        content.Children.Add(new TextBlock
        {
            Text = AppLocalization.Format("UpdatesRollBackMessage", name, point.Version, point.ReplacedBy),
            TextWrapping = TextWrapping.Wrap
        });
        CheckBox? restoreData = null;
        if (runner.HasDataBackup(id))
        {
            restoreData = new CheckBox { Content = AppLocalization.Get("UpdatesRollBackRestoreData") };
            content.Children.Add(restoreData);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            FlowDirection = AppLocalization.LayoutDirection,
            Title = AppLocalization.Format("UpdatesRollBackTitle", name),
            Content = content,
            PrimaryButtonText = AppLocalization.Get("UpdatesRollBack"),
            CloseButtonText = AppLocalization.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        SetBusy(true, AppLocalization.Format("UpdatesRollingBack", name));
        try
        {
            var restored = await runner.RollBackAsync(id, restoreData?.IsChecked == true);
            finished.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (loaded)
                ShowStatus(
                    InfoBarSeverity.Success,
                    AppLocalization.Get("UpdatesRolledBackTitle"),
                    AppLocalization.Format("UpdatesRolledBackMessage", name, restored.Version)
                );
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (loaded)
                ShowStatus(InfoBarSeverity.Error, AppLocalization.Get("UpdatesRollBackFailedTitle"), error.Message);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
        if (!loaded) return;
        RenderAll();
        await LoadInstalledAsync();
    }

    private static string ComponentCategory(ManagedComponentUpdate update)
    {
        return AppLocalization.Get(update.PageTag switch
        {
            "php" => "UpdatesCategoryPhp",
            "node" => "UpdatesCategoryNode",
            "services" => "UpdatesCategoryService",
            "debugger" => "UpdatesCategoryDebugger",
            _ => "UpdatesCategoryTool"
        });
    }
}
