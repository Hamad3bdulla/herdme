namespace HerdMe.Windows.Models;

public sealed class WindowsSiteSettings
{
    public List<string> Roots { get; set; } = [];

    public List<string> LinkedSites { get; set; } = [];

    public string Tld { get; set; } = "test";

    public bool StartAutomatically { get; set; }

    public bool ShowPreviews { get; set; } = true;

    public bool AutomaticUpdates { get; set; } = true;

    public string UpdateChannel { get; set; } = "Stable";
}
