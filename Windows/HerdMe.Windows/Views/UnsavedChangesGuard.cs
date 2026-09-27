using HerdMe.Windows.Services;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Views;

// Editor dialogs: Cancel, Esc or the close button on a dirty editor first shows a warning;
// only a second close discards. Saving (the primary button) always closes normally.
public sealed class UnsavedChangesGuard
{
    private readonly Func<bool> isDirty;
    private readonly Action<string> warn;
    private bool armed;

    private UnsavedChangesGuard(Func<bool> isDirty, Action<string> warn)
    {
        this.isDirty = isDirty;
        this.warn = warn;
    }

    public static UnsavedChangesGuard Attach(ContentDialog dialog, Func<bool> isDirty, Action<string> warn)
    {
        var guard = new UnsavedChangesGuard(isDirty, warn);
        dialog.Closing += guard.Dialog_Closing;
        return guard;
    }

    // Call when the text changes again so the next close asks again.
    public void Reset() => armed = false;

    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (args.Result == ContentDialogResult.Primary || armed || !isDirty()) return;
        args.Cancel = true;
        armed = true;
        warn(AppLocalization.Get("UnsavedChangesDiscardHint"));
    }
}
