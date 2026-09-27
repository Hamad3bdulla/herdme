using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows;

// First-run feature tips. Each tip opens at most once ever (remembered in settings), at most
// one automatic tip opens per launch, and none open over onboarding or What's new.
public sealed partial class MainWindow
{
    internal const string StatusBarTipId = "status-bar";
    internal const string HealthTabTipId = "dashboard-health";
    internal const string LanguageTipId = "general-language";

    private HashSet<string>? seenTips;
    private bool automaticTipShown;

    internal bool OfferTip(TeachingTip tip, string tipId, bool automatic = true)
    {
        if (shuttingDown || RequiresOnboarding || pendingWhatsNewVersion is not null) return false;
        if (automatic && automaticTipShown) return false;
        if (tip.Target is FrameworkElement { IsLoaded: false } or FrameworkElement { ActualWidth: 0 }) return false;
        seenTips ??= new HashSet<string>(services.SiteSettings.Load().SeenTips ?? [], StringComparer.Ordinal);
        if (!seenTips.Add(tipId)) return false;
        services.SiteSettings.MarkTipSeen(tipId);
        if (automatic) automaticTipShown = true;
        tip.IsOpen = true;
        return true;
    }

    // Called from the dashboard once it has loaded: the Health tab first, the status bar next launch.
    internal void OfferShellTips(TeachingTip healthTabTip)
    {
        if (!OfferTip(healthTabTip, HealthTabTipId) && StatusBar.Visibility == Visibility.Visible)
        {
            OfferTip(StatusBarTip, StatusBarTipId);
        }
    }
}
