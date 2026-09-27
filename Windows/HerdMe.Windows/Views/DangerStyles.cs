using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Views;

// One look and one rule for destructive actions: a red primary button, Cancel as the
// default (Enter and Esc never destroy anything), and the same Cancel wording everywhere.
public static class DangerStyles
{
    public static Style Button => (Style)Application.Current.Resources["DangerButtonStyle"];

    public static ContentDialog Apply(ContentDialog dialog)
    {
        dialog.PrimaryButtonStyle = Button;
        dialog.DefaultButton = ContentDialogButton.Close;
        dialog.CloseButtonText = AppLocalization.Get("CommonCancel");
        dialog.FlowDirection = AppLocalization.LayoutDirection;
        return dialog;
    }

    public static async Task<bool> ConfirmAsync(XamlRoot xamlRoot, string title, string message, string action)
    {
        var dialog = Apply(new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action
        });
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another dialog is already open; do nothing destructive.
            return false;
        }
    }
}
