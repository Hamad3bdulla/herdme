using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace HerdMe.Windows;

// Density and motion for the whole window. Compact density merges the WinUI compact sizing
// dictionary at the root; motion follows Windows "Animation effects" plus the General switch.
public sealed partial class MainWindow
{
    private static readonly Uri CompactDensitySource = new("ms-appx:///Microsoft.UI.Xaml/DensityStyles/Compact.xaml");
    private readonly global::Windows.UI.ViewManagement.UISettings uiSettings = new();
    private bool motionSubscribed;
    private ResourceDictionary? compactDensity;

    public bool MotionEnabled { get; private set; } = true;

    private void InitializeAppearance(bool compact)
    {
        ApplyCompactPreference(compact);
        ApplyMotionPreference();
        if (motionSubscribed) return;
        uiSettings.AnimationsEnabledChanged += UiSettings_AnimationsEnabledChanged;
        motionSubscribed = true;
    }

    private void ReleaseAppearance()
    {
        if (!motionSubscribed) return;
        uiSettings.AnimationsEnabledChanged -= UiSettings_AnimationsEnabledChanged;
        motionSubscribed = false;
    }

    private void UiSettings_AnimationsEnabledChanged(
        global::Windows.UI.ViewManagement.UISettings sender,
        global::Windows.UI.ViewManagement.UISettingsAnimationsEnabledChangedEventArgs args
    )
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!shuttingDown) ApplyMotionPreference();
        });
    }

    // General > Compact interface. Pages opened from now on use the compact sizes; pages that
    // are already open keep theirs until HerdMe starts again (the setting says so).
    internal void ApplyCompactPreference(bool compact)
    {
        var dictionaries = RootLayout.Resources.MergedDictionaries;
        if (!compact)
        {
            if (compactDensity is not null) dictionaries.Remove(compactDensity);
            compactDensity = null;
            return;
        }
        if (compactDensity is not null) return;
        try
        {
            compactDensity = new ResourceDictionary { Source = CompactDensitySource };
            dictionaries.Add(compactDensity);
        }
        catch (COMException error)
        {
            compactDensity = null;
            System.Diagnostics.Debug.WriteLine($"HerdMe could not load compact density: {error.Message}");
        }
    }

    internal void ApplyMotionPreference()
    {
        bool systemAnimations;
        try
        {
            systemAnimations = uiSettings.AnimationsEnabled;
        }
        catch (COMException)
        {
            systemAnimations = true;
        }
        MotionEnabled = systemAnimations && !services.SiteSettings.Load().ReduceMotion;
        ContentFrame.ContentTransitions = MotionEnabled
            ? new TransitionCollection { new EntranceThemeTransition { FromHorizontalOffset = 0, FromVerticalOffset = 16 } }
            : new TransitionCollection();
        ToastHost.OpacityTransition = MotionEnabled ? new ScalarTransition() : null;
    }
}
