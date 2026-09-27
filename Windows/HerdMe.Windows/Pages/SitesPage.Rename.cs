using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace HerdMe.Windows.Pages;

// F2 on a Sites row: rename in place. The .test name is checked while typing; Enter renames
// the folder (the domain follows it), Esc or leaving the box cancels.
public sealed partial class SitesPage
{
    private SitesListItem? renamingItem;
    private bool committingRename;

    private void RenameSiteFromMenu_Click(object sender, RoutedEventArgs e)
    {
        if (SelectSiteFromMenu(sender) is { } site) BeginRename(site);
    }

    private void BeginRename(SiteRecord site)
    {
        if (!loaded) return;
        var item = VisibleSites.FirstOrDefault(row => ReferenceEquals(row.Site, site));
        if (item is null) return;
        if (RenameBlockedReason(site) is { } blocked)
        {
            App.MainWindow.ShowToast(blocked);
            return;
        }
        CancelRename();
        renamingItem = item;
        item.SetRenaming(true);
        SiteList.ScrollIntoView(item);
        // The box becomes visible on the next layout pass; focus it then.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(renamingItem, item) || RenameBox(item) is not { } box) return;
            box.Text = SiteRenamer.Normalize(site.Domain, settingsStore.Load().Tld);
            box.SelectAll();
            box.Focus(FocusState.Programmatic);
            ValidateRename(item, box.Text);
        });
    }

    private string? RenameBlockedReason(SiteRecord site)
    {
        if (shares.Find(site.Domain) is not null) return AppLocalization.Get("SitesRenameBlockedShared");
        var running = site.IsRunning || Enum.GetValues<SiteBackgroundProcessKind>()
            .Any(kind => siteProcesses.State(site.Path, kind).Running);
        return running ? AppLocalization.Get("SitesRenameBlockedRunning") : null;
    }

    private TextBox? RenameBox(SitesListItem item)
    {
        return SiteList.ContainerFromItem(item) is DependencyObject container
            ? FindDescendant<TextBox>(container, box => box.Tag is string path
                && path.Equals(item.Path, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed && match(typed)) return typed;
            if (FindDescendant(child, match) is { } found) return found;
        }
        return null;
    }

    private SiteNameProblem ValidateRename(SitesListItem item, string text)
    {
        var tld = settingsStore.Load().Tld;
        var label = SiteRenamer.Normalize(text, tld);
        var taken = Sites.Where(site => !ReferenceEquals(site, item.Site)).Select(site => site.Domain)
            .Concat(proxySites.Load().Select(proxy => proxy.Domain(tld)));
        var targetExists = false;
        try
        {
            if (label.Length > 0 && !label.Equals(SiteRenamer.Normalize(item.Site.Domain, tld), StringComparison.Ordinal))
            {
                var target = SiteRenamer.TargetPath(item.Site.Path, label);
                targetExists = !target.Equals(item.Site.Path, StringComparison.OrdinalIgnoreCase)
                    && (Directory.Exists(target) || File.Exists(target));
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException)
        {
            targetExists = true;
        }
        var problem = SiteRenamer.Validate(label, item.Site.Domain, tld, taken, targetExists);
        var message = problem == SiteNameProblem.None
            ? AppLocalization.Format("SitesRenameAvailable", $"{label}.{tld}")
            : AppLocalization.Get(SiteRenamer.MessageKey(problem));
        item.SetRenameMessage(message, problem is not (SiteNameProblem.None or SiteNameProblem.Unchanged));
        return problem;
    }

    private void RenameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box && renamingItem is { } item) ValidateRename(item, box.Text);
    }

    private async void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box || renamingItem is not { } item) return;
        switch (e.Key)
        {
            case global::Windows.System.VirtualKey.Enter:
                e.Handled = true;
                await CommitRenameAsync(item, box.Text);
                break;
            case global::Windows.System.VirtualKey.Escape:
                e.Handled = true;
                CancelRename();
                SiteList.Focus(FocusState.Keyboard);
                break;
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!committingRename) CancelRename();
    }

    private void CancelRename()
    {
        if (renamingItem is not { } item) return;
        renamingItem = null;
        item.SetRenaming(false);
    }

    private async Task CommitRenameAsync(SitesListItem item, string text)
    {
        var problem = ValidateRename(item, text);
        if (problem == SiteNameProblem.Unchanged)
        {
            CancelRename();
            return;
        }
        if (problem != SiteNameProblem.None) return;
        var site = item.Site;
        var label = SiteRenamer.Normalize(text, settingsStore.Load().Tld);
        committingRename = true;
        try
        {
            var target = SiteRenamer.TargetPath(site.Path, label);
            await Task.Run(() => SiteRenamer.MoveFolder(site.Path, target));
            settingsStore.MoveSitePath(site.Path, target);
            MoveThumbnail(site.Path, target);
            CancelRename();
            RequestSelectSite(target);
            await ScanAsync();
            App.MainWindow.ShowToast(AppLocalization.Format("SitesRenamed", site.Domain, label));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException)
        {
            // Windows keeps the folder when a file inside it is open (an editor, a terminal).
            item.SetRenameMessage(AppLocalization.Format("SitesRenameFailed", error.Message), true);
        }
        finally
        {
            committingRename = false;
        }
    }

    private void MoveThumbnail(string from, string to)
    {
        try
        {
            var source = SiteQuickProbe.ThumbnailPath(settingsStore.SupportRoot, from);
            if (File.Exists(source))
            {
                File.Move(source, SiteQuickProbe.ThumbnailPath(settingsStore.SupportRoot, to), overwrite: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
