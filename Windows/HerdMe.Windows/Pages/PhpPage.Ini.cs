using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HerdMe.Windows.Pages;

// php.ini for the selected PHP version: find (Enter / F3 next, Shift+F3 previous), edit,
// save. Saving refuses a file that drops a required extension, keeps php.ini.bak, and
// refuses to overwrite a file that changed on disk since it was opened (an extension toggle
// above rewrites php.ini too).
public sealed partial class PhpPage
{
    private string? iniCycle;
    private string iniLoadedText = string.Empty;
    private DateTime iniLoadedWriteTime;
    private bool iniLoading;
    private IReadOnlyList<int> iniMatches = [];

    private bool IniDirty => iniCycle is not null && !string.Equals(IniEditor.Text, iniLoadedText, StringComparison.Ordinal);

    private void InitializeIniEditor()
    {
        ToolTipService.SetToolTip(IniPreviousButton, AppLocalization.Get("PhpIniPreviousTooltip"));
        ToolTipService.SetToolTip(IniNextButton, AppLocalization.Get("PhpIniNextTooltip"));
    }

    // Called from RefreshAsync. Unsaved edits for the same version are kept, not replaced.
    private void LoadIniForCycle(string cycle, bool force)
    {
        if (!force && IniDirty && string.Equals(iniCycle, cycle, StringComparison.Ordinal))
        {
            if (IniChangedOnDisk()) IniStatusText.Text = AppLocalization.Get("PhpIniChangedOnDisk");
            return;
        }
        var path = PhpIniEditor.ConfigurationPath(runtimeInstaller.RuntimeRoot, cycle);
        iniLoading = true;
        try
        {
            if (!File.Exists(path))
            {
                iniCycle = null;
                iniLoadedText = string.Empty;
                IniEditor.Text = string.Empty;
                IniEditor.IsEnabled = false;
                IniStatusText.Text = AppLocalization.Get("PhpIniNotInstalled");
                return;
            }
            var text = File.ReadAllText(path);
            iniCycle = cycle;
            iniLoadedWriteTime = File.GetLastWriteTimeUtc(path);
            IniEditor.Text = text;
            // TextBox keeps line breaks as a bare CR; compare against what it holds.
            iniLoadedText = IniEditor.Text;
            IniEditor.IsEnabled = true;
            IniStatusText.Text = path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            iniCycle = null;
            IniEditor.IsEnabled = false;
            IniStatusText.Text = AppLocalization.Format("PhpIniReadFailed", error.Message);
        }
        finally
        {
            iniLoading = false;
            UpdateIniState();
            UpdateIniMatches(select: false);
        }
    }

    private bool IniChangedOnDisk()
    {
        if (iniCycle is null) return false;
        try
        {
            var path = PhpIniEditor.ConfigurationPath(runtimeInstaller.RuntimeRoot, iniCycle);
            return File.Exists(path) && File.GetLastWriteTimeUtc(path) != iniLoadedWriteTime;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void UpdateIniState()
    {
        IniSaveButton.IsEnabled = IniDirty && !iniLoading;
    }

    private void IniEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (iniLoading) return;
        UpdateIniState();
        if (IniDirty) IniStatusText.Text = AppLocalization.Get("PhpIniUnsaved");
        UpdateIniMatches(select: false);
    }

    private void IniSearch_TextChanged(object sender, TextChangedEventArgs e) => UpdateIniMatches(select: true);

    private void IniSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Shift)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key is global::Windows.System.VirtualKey.Enter or global::Windows.System.VirtualKey.F3)
        {
            e.Handled = true;
            MoveToMatch(forward: !shift);
        }
    }

    private void IniPrevious_Click(object sender, RoutedEventArgs e) => MoveToMatch(forward: false);

    private void IniNext_Click(object sender, RoutedEventArgs e) => MoveToMatch(forward: true);

    private void UpdateIniMatches(bool select)
    {
        var query = IniSearchBox.Text;
        iniMatches = PhpIniEditor.FindMatches(IniEditor.Text, query);
        var any = iniMatches.Count > 0;
        IniPreviousButton.IsEnabled = any;
        IniNextButton.IsEnabled = any;
        IniMatchText.Text = query.Length == 0
            ? string.Empty
            : any
                ? AppLocalization.Format("PhpIniMatchCount", iniMatches.Count)
                : AppLocalization.Get("PhpIniNoMatches");
        if (select && any) SelectMatch(iniMatches[0], focusEditor: false);
    }

    private void MoveToMatch(bool forward)
    {
        if (iniMatches.Count == 0) return;
        var target = PhpIniEditor.NextMatch(iniMatches, IniEditor.SelectionStart, forward);
        if (target < 0) return;
        SelectMatch(target, focusEditor: false);
        var position = iniMatches.ToList().IndexOf(target) + 1;
        IniMatchText.Text = AppLocalization.Format("PhpIniMatchPosition", position, iniMatches.Count);
    }

    // The search box keeps focus while stepping through matches; the selection still shows.
    private void SelectMatch(int start, bool focusEditor)
    {
        var length = Math.Min(IniSearchBox.Text.Length, Math.Max(0, IniEditor.Text.Length - start));
        IniEditor.Select(start, length);
        if (focusEditor) IniEditor.Focus(FocusState.Programmatic);
    }

    private void IniReload_Click(object sender, RoutedEventArgs e)
    {
        var cycle = PhpCycleBox.SelectedItem?.ToString() ?? settings.PhpCycle;
        LoadIniForCycle(cycle, force: true);
    }

    private async void IniSave_Click(object sender, RoutedEventArgs e) => await SaveIniAsync();

    private async Task SaveIniAsync()
    {
        if (iniCycle is not { } cycle || !IniDirty) return;
        if (IniChangedOnDisk())
        {
            IniStatusText.Text = AppLocalization.Get("PhpIniChangedOnDisk");
            return;
        }
        var text = IniEditor.Text;
        var path = PhpIniEditor.ConfigurationPath(runtimeInstaller.RuntimeRoot, cycle);
        IniSaveButton.IsEnabled = false;
        try
        {
            var disabled = runtimeInstaller.ExplicitlyDisabledExtensions(cycle);
            var problem = await Task.Run(() => PhpIniEditor.Validate(text, disabled));
            if (problem != PhpIniProblem.None)
            {
                IniStatusText.Text = AppLocalization.Get(PhpIniEditor.MessageKey(problem));
                return;
            }
            await Task.Run(() => PhpIniEditor.SaveAtomically(path, text));
            PhpModuleProbeCache.Invalidate(Path.GetDirectoryName(path) ?? path);
            iniLoadedText = text;
            iniLoadedWriteTime = File.GetLastWriteTimeUtc(path);
            IniStatusText.Text = AppLocalization.Get("PhpIniSaved");
            App.MainWindow.ShowToast(AppLocalization.Format("PhpIniSavedToast", cycle));
            await RefreshAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            if (XamlRoot is not null) await ErrorDialog.ShowAsync(XamlRoot, error);
            IniStatusText.Text = AppLocalization.Format("PhpIniSaveFailed", error.Message);
        }
        finally
        {
            UpdateIniState();
        }
    }

    // Ctrl+S inside the editor saves php.ini, not the settings above.
    private async void IniEditor_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.S) return;
        var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!control) return;
        e.Handled = true;
        await SaveIniAsync();
    }
}
