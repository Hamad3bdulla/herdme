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

    public bool MotionEnabled { get; private set; } = true;

    private void InitializeAppearance(bool compact)
    {
        if (compact)
        {
            try
            {
                RootLayout.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = CompactDensitySource });
            }
            catch (COMException error)
            {
                System.Diagnostics.Debug.WriteLine($"HerdMe could not load compact density: {error.Message}");
            }
        }
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
