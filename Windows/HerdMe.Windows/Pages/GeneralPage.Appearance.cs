using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

// Language and motion. The language is applied once at startup (App.ApplyLanguageOverride),
// so a change only shows the restart note; motion applies to the window right away.
public sealed partial class GeneralPage
{
    // Native names are never translated so a user can always find their own language.
    private const string ArabicNativeName = "\u0627\u0644\u0639\u0631\u0628\u064A\u0629";
    private bool loadingAppearance;
    private string startupLanguage = UiLanguageSettings.System;

    private void LoadAppearance(Models.WindowsSiteSettings settings)
    {
        loadingAppearance = true;
        startupLanguage = UiLanguageSettings.Normalize(settings.UiLanguage);
        LanguageBox.Items.Clear();
        LanguageBox.Items.Add(new ComboBoxItem
        {
            Content = AppLocalization.Get("GeneralLanguageSystem"),
            Tag = UiLanguageSettings.System
        });
        LanguageBox.Items.Add(new ComboBoxItem { Content = "English", Tag = UiLanguageSettings.English, Language = "en-US" });
        LanguageBox.Items.Add(new ComboBoxItem
        {
            Content = ArabicNativeName,
            Tag = UiLanguageSettings.Arabic,
            Language = "ar",
            FlowDirection = FlowDirection.RightToLeft
        });
        var index = UiLanguageSettings.Supported.ToList().IndexOf(startupLanguage);
        LanguageBox.SelectedIndex = Math.Max(0, index);
        ReduceMotionToggle.IsOn = settings.ReduceMotion;
        ShowStatusBarToggle.IsOn = settings.ShowStatusBar;
        loadingAppearance = false;
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingAppearance || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        settingsStore.UpdateUiLanguage(language);
        LanguageRestartNote.Visibility = language == startupLanguage
            ? Visibility.Collapsed
            : Visibility.Visible;
        App.MainWindow.ShowToast(AppLocalization.Get("CommonSavedToast"));
    }

    private void ReduceMotionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (loadingAppearance) return;
        settingsStore.UpdateReduceMotion(ReduceMotionToggle.IsOn);
        App.MainWindow.ApplyMotionPreference();
    }

    private void ShowStatusBarToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (loadingAppearance) return;
        settingsStore.UpdateShowStatusBar(ShowStatusBarToggle.IsOn);
        App.MainWindow.ApplyStatusBarPreference(ShowStatusBarToggle.IsOn);
    }
}
