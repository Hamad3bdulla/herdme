using System.ComponentModel;
using System.Globalization;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;

namespace HerdMe.Windows.ViewModels;

/// <summary>
/// Observable row for the Sites list. Rows are kept across scans, searches, and
/// process events so the ListView keeps its containers, selection, and scroll
/// position; only the properties that changed raise notifications.
/// </summary>
public sealed class SitesListItem : INotifyPropertyChanged
{
    private string domain = string.Empty;
    private string framework = string.Empty;
    private string? gitSummary;
    private bool isFavorite;
    private string? workflowStatus;
    private string? lastError;
    private bool isRunning;
    private bool isShared;
    private string? phpVersion;
    private bool usesPhp;

    public SitesListItem(SiteRecord site)
    {
        Site = site;
        Path = site.Path;
        Update(site);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SiteRecord Site { get; private set; }

    public string Path { get; }

    public string Domain => domain;

    public string Framework => framework;

    public string? GitSummary => gitSummary;

    public bool IsFavorite => isFavorite;

    public Visibility FavoriteVisibility => isFavorite ? Visibility.Visible : Visibility.Collapsed;

    public string? WorkflowStatus => workflowStatus;

    public string? LastError => lastError;

    public Visibility LastErrorVisibility => string.IsNullOrWhiteSpace(lastError)
        ? Visibility.Collapsed
        : Visibility.Visible;

    // Framework tile: a monogram for known frameworks, a globe for plain sites.
    public string Monogram => FrameworkVisuals.Monogram(framework);

    public Visibility MonogramVisibility => Monogram.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GlobeVisibility => Monogram.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

    public Style TileStyle => FrameworkVisuals.TileStyle(framework);

    public Style MonogramStyle => FrameworkVisuals.MonogramStyle(framework);

    // Corner dot on the tile: red after a failure, green while something runs.
    public Visibility StatusDotVisibility => HasError || isRunning ? Visibility.Visible : Visibility.Collapsed;

    public Style StatusDotStyle => StatusStyles.Dot(HasError ? StatusTone.Critical : StatusTone.Success);

    public Visibility SharedVisibility => isShared ? Visibility.Visible : Visibility.Collapsed;

    // Inline PHP picker: Node.js sites have no PHP runtime, so the chip is hidden for them.
    public string PhpLabel => phpVersion is null ? "PHP" : $"PHP {phpVersion}";

    public Visibility PhpPickerVisibility => usesPhp ? Visibility.Visible : Visibility.Collapsed;

    private bool HasError => !string.IsNullOrWhiteSpace(lastError);

    // Inline rename (F2): the row swaps its domain text for a text box and a live message.
    private bool isRenaming;
    private string renameMessage = string.Empty;
    private bool renameMessageIsError;

    public bool IsRenaming => isRenaming;

    public Visibility DisplayVisibility => isRenaming ? Visibility.Collapsed : Visibility.Visible;

    public Visibility RenameVisibility => isRenaming ? Visibility.Visible : Visibility.Collapsed;

    public string RenameMessage => renameMessage;

    public Style RenameMessageStyle => (Style)Application.Current.Resources[
        renameMessageIsError ? "StatusCriticalTextStyle" : "CaptionTextStyle"
    ];

    public void SetRenaming(bool renaming)
    {
        if (isRenaming == renaming) return;
        isRenaming = renaming;
        if (!renaming) SetRenameMessage(string.Empty, false);
        Raise(nameof(IsRenaming));
        Raise(nameof(DisplayVisibility));
        Raise(nameof(RenameVisibility));
    }

    public void SetRenameMessage(string message, bool isError)
    {
        if (string.Equals(renameMessage, message, StringComparison.Ordinal)
            && renameMessageIsError == isError) return;
        renameMessage = message;
        renameMessageIsError = isError;
        Raise(nameof(RenameMessage));
        Raise(nameof(RenameMessageStyle));
    }

    public void Update(SiteRecord site)
    {
        Site = site;
        if (!string.Equals(domain, site.Domain, StringComparison.Ordinal))
        {
            domain = site.Domain;
            Raise(nameof(Domain));
        }
        if (!string.Equals(framework, site.Framework, StringComparison.Ordinal))
        {
            framework = site.Framework;
            Raise(nameof(Framework));
            Raise(nameof(Monogram));
            Raise(nameof(MonogramVisibility));
            Raise(nameof(GlobeVisibility));
            Raise(nameof(TileStyle));
            Raise(nameof(MonogramStyle));
        }
        if (!string.Equals(gitSummary, site.GitSummary, StringComparison.Ordinal))
        {
            gitSummary = site.GitSummary;
            Raise(nameof(GitSummary));
        }
        if (isFavorite != site.IsFavorite)
        {
            isFavorite = site.IsFavorite;
            Raise(nameof(IsFavorite));
            Raise(nameof(FavoriteVisibility));
        }
        if (!string.Equals(workflowStatus, site.WorkflowStatus, StringComparison.Ordinal))
        {
            workflowStatus = site.WorkflowStatus;
            Raise(nameof(WorkflowStatus));
        }
        var statusChanged = false;
        if (!string.Equals(lastError, site.LastError, StringComparison.Ordinal))
        {
            lastError = site.LastError;
            statusChanged = true;
            Raise(nameof(LastError));
            Raise(nameof(LastErrorVisibility));
        }
        if (isRunning != site.IsRunning)
        {
            isRunning = site.IsRunning;
            statusChanged = true;
        }
        if (statusChanged)
        {
            Raise(nameof(StatusDotVisibility));
            Raise(nameof(StatusDotStyle));
        }
        if (!string.Equals(phpVersion, site.PhpVersion, StringComparison.Ordinal))
        {
            phpVersion = site.PhpVersion;
            Raise(nameof(PhpLabel));
        }
        var php = !site.Framework.Equals("Node.js", StringComparison.OrdinalIgnoreCase);
        if (usesPhp != php)
        {
            usesPhp = php;
            Raise(nameof(PhpPickerVisibility));
        }
        if (isShared != site.IsShared)
        {
            isShared = site.IsShared;
            Raise(nameof(SharedVisibility));
        }
    }

    // Row warning badge (missing PHP, .env, APP_KEY or vendor); the flyout offers the fixes.
    private IReadOnlyList<SiteWarning> warnings = [];

    public IReadOnlyList<SiteWarning> Warnings => warnings;

    public Visibility WarningVisibility => warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string WarningCount => warnings.Count.ToString(CultureInfo.CurrentCulture);

    public string WarningSummary => string.Join("\n", warnings.Select(SiteWarningText.Describe));

    public void SetWarnings(IReadOnlyList<SiteWarning> next)
    {
        if (warnings.SequenceEqual(next)) return;
        warnings = next;
        Raise(nameof(Warnings));
        Raise(nameof(WarningVisibility));
        Raise(nameof(WarningCount));
        Raise(nameof(WarningSummary));
    }

    private void Raise(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

// Framework names come from the core scanner: Laravel, WordPress, PHP, Node.js, or Site.
internal static class FrameworkVisuals
{
    public static string Monogram(string framework) => Key(framework) switch
    {
        "Laravel" => "L",
        "WordPress" => "W",
        "Php" => "P",
        "Node" => "N",
        _ => string.Empty
    };

    public static Style TileStyle(string framework) =>
        (Style)Application.Current.Resources[$"SiteTile{Key(framework)}Style"];

    public static Style MonogramStyle(string framework) =>
        (Style)Application.Current.Resources[$"SiteMonogram{Key(framework)}Style"];

    private static string Key(string framework) => framework switch
    {
        "Laravel" => "Laravel",
        "WordPress" => "WordPress",
        "PHP" => "Php",
        "Node.js" => "Node",
        _ => "Site"
    };
}

internal static class SiteWarningText
{
    public static string Describe(SiteWarning warning) => warning.Kind switch
    {
        SiteWarningKind.PhpNotInstalled => AppLocalization.Format("SitesWarningPhpNotInstalled", warning.Detail),
        SiteWarningKind.EnvironmentMissing => AppLocalization.Get("SitesWarningEnvironmentMissing"),
        SiteWarningKind.AppKeyMissing => AppLocalization.Get("SitesWarningAppKeyMissing"),
        SiteWarningKind.DependenciesMissing => AppLocalization.Get("SitesWarningDependenciesMissing"),
        _ => warning.Kind.ToString()
    };

    public static string FixLabel(SiteWarning warning) => warning.Kind switch
    {
        SiteWarningKind.PhpNotInstalled => AppLocalization.Format("SitesWarningFixInstallPhp", warning.Detail),
        SiteWarningKind.EnvironmentMissing => AppLocalization.Get("SitesWarningFixEnvironment"),
        SiteWarningKind.AppKeyMissing => AppLocalization.Get("SitesWarningFixAppKey"),
        _ => AppLocalization.Get("SitesWarningFixDependencies")
    };
}
