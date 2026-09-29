using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

// General > Windows integration > "Speed up PHP": an opt-in Microsoft Defender exclusion for the
// HerdMe folder only. Turning it on first explains what it does and that Windows will ask for
// administrator approval; declining either prompt leaves the switch off.
public sealed partial class GeneralPage
{
    private bool loadingDefenderExclusion;
    private bool applyingDefenderExclusion;

    private void LoadDefenderExclusion(WindowsSiteSettings settings)
    {
        loadingDefenderExclusion = true;
        DefenderExclusionToggle.IsOn = !string.IsNullOrEmpty(settings.DefenderExclusionPath);
        loadingDefenderExclusion = false;
        DefenderExclusionStatusText.Visibility = Visibility.Collapsed;
    }

    private void SetDefenderToggle(bool on)
    {
        loadingDefenderExclusion = true;
        DefenderExclusionToggle.IsOn = on;
        loadingDefenderExclusion = false;
    }

    private async void DefenderExclusionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (loadingDefenderExclusion || applyingDefenderExclusion) return;
        var add = DefenderExclusionToggle.IsOn;
        var path = add ? DefenderExclusion.DefaultPath : settingsStore.Load().DefenderExclusionPath;
        if (string.IsNullOrEmpty(path))
        {
            // Nothing was added by HerdMe, so there is nothing to remove.
            return;
        }

        if (add && !await ConfirmDefenderExclusionAsync(path))
        {
            SetDefenderToggle(false);
            return;
        }

        applyingDefenderExclusion = true;
        DefenderExclusionToggle.IsEnabled = false;
        DefenderExclusionStatusText.Text = AppLocalization.Get("GeneralDefenderExclusionWaiting");
        DefenderExclusionStatusText.Visibility = Visibility.Visible;
        try
        {
            var result = await DefenderExclusion.ApplyAsync(add, path);
            if (result.Outcome == DefenderExclusionOutcome.Applied)
            {
                settingsStore.UpdateDefenderExclusion(add ? path : string.Empty);
                DefenderExclusionStatusText.Visibility = Visibility.Collapsed;
                App.MainWindow.ShowToast(result.Message);
                return;
            }

            SetDefenderToggle(!add);
            DefenderExclusionStatusText.Visibility = Visibility.Collapsed;
            if (result.Outcome == DefenderExclusionOutcome.Declined)
            {
                App.MainWindow.ShowToast(result.Message);
                return;
            }
            var dialog = new ContentDialog
            {
                FlowDirection = AppLocalization.LayoutDirection,
                XamlRoot = XamlRoot,
                Title = AppLocalization.Get("GeneralDefenderExclusionFailedTitle"),
                Content = new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = AppLocalization.Get("CommonOk")
            };
            await dialog.ShowAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetDefenderToggle(!add);
            DefenderExclusionStatusText.Text = error.Message;
            DefenderExclusionStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            applyingDefenderExclusion = false;
            DefenderExclusionToggle.IsEnabled = true;
        }
    }

    private async Task<bool> ConfirmDefenderExclusionAsync(string path)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = AppLocalization.Get("GeneralDefenderExclusionConfirmBody"),
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text = path,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FlowDirection = FlowDirection.LeftToRight,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
        body.Children.Add(new TextBlock
        {
            Text = AppLocalization.Get("GeneralDefenderExclusionConfirmRisk"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["SettingsRowDescriptionStyle"]
        });
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Get("GeneralDefenderExclusionConfirmTitle"),
            Content = body,
            PrimaryButtonText = AppLocalization.Get("GeneralDefenderExclusionConfirmButton"),
            CloseButtonText = AppLocalization.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
