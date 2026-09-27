using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Views;

// Shows an InputCheck in the small text line under a field: red for errors, caution for
// warnings, quiet for "looks good". Screen readers hear the change and the field's help text.
public static class FieldValidation
{
    public static void Show(TextBlock message, Control field, InputCheck check, string? suffix = null)
    {
        var text = check.MessageKey.Length == 0
            ? string.Empty
            : check.Argument is null
                ? AppLocalization.Get(check.MessageKey)
                : AppLocalization.Format(check.MessageKey, check.Argument);
        if (text.Length > 0 && !string.IsNullOrWhiteSpace(suffix)) text = $"{text} {suffix}";
        message.Text = text;
        message.Style = (Style)Application.Current.Resources[check.Severity switch
        {
            InputSeverity.Error => "StatusCriticalTextStyle",
            InputSeverity.Warning => "StatusCautionTextStyle",
            _ => "CaptionTextStyle"
        }];
        message.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);
        AutomationProperties.SetHelpText(field, check.Severity == InputSeverity.Valid ? string.Empty : text);
        if (text.Length > 0 && FrameworkElementAutomationPeer.FromElement(message) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
