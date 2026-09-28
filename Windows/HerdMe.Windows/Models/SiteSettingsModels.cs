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

    // Opt-in: Windows notifications with buttons (Open mail, Restart service, Open site).
    // Off keeps the tray balloon, which needs no Windows registration.
    public bool ActionNotifications { get; set; }

    // A notification with the subject and an Open button when a mail is captured while the
    // Mail page is not in front. Uses the same switch and throttle as the other notifications.
    public bool MailNotifications { get; set; } = true;

    // Width of the Sites list pane, set by dragging the splitter.
    public double SitesListWidth { get; set; } = SitesListWidthDefault;

    public const double SitesListWidthDefault = 280;

    public const double SitesListWidthMinimum = 240;

    public const double SitesListWidthMaximum = 560;

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

    // Dashboard "Getting started" steps already done (GettingStarted.Step* ids).
    public List<string> GettingStartedSteps { get; set; } = [];

    // The user closed the checklist before finishing it.
    public bool GettingStartedDismissed { get; set; }
}
