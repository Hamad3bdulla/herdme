namespace HerdMe.Windows.Models;

public sealed class WindowsSiteSettings
{
    public int SchemaVersion { get; set; }

    public List<string> Roots { get; set; } = [];

    public List<string> LinkedSites { get; set; } = [];

    public List<string> FavoriteSites { get; set; } = [];

    public string Tld { get; set; } = "test";

    public bool StartAutomatically { get; set; } = true;

    public bool ShowPreviews { get; set; } = true;

    public bool CompactMode { get; set; }

    public bool ShowNotifications { get; set; } = true;

    // "" follows Windows; otherwise one of UiLanguageSettings.Supported. Applied at startup.
    public string UiLanguage { get; set; } = string.Empty;

    // Turns off page transitions and fades even when Windows animations are on.
    public bool ReduceMotion { get; set; }

    public bool AutomaticUpdates { get; set; } = true;

    public string UpdateChannel { get; set; } = "Stable";

    public bool OnboardingCompleted { get; set; }

    // The HerdMe version whose What's new the user has seen; empty on a new installation.
    public string LastSeenVersion { get; set; } = string.Empty;

    // Feature tips (TeachingTip ids) already shown once.
    public List<string> SeenTips { get; set; } = [];
}
