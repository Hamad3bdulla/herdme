namespace HerdMe.Windows.Models;

public sealed class WindowsSiteSettings
{
    public int SchemaVersion { get; set; }

    public List<string> Roots { get; set; } = [];

    public List<string> LinkedSites { get; set; } = [];

    public string Tld { get; set; } = "test";

    public bool StartAutomatically { get; set; } = true;

    public bool ShowPreviews { get; set; } = true;

    public bool CompactMode { get; set; }

    public bool AutomaticUpdates { get; set; } = true;

    public string UpdateChannel { get; set; } = "Stable";

    public bool OnboardingCompleted { get; set; }
}
