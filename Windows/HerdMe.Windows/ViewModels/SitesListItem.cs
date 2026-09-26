using System.ComponentModel;
using HerdMe.Windows.Models;
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
        if (!string.Equals(lastError, site.LastError, StringComparison.Ordinal))
        {
            lastError = site.LastError;
            Raise(nameof(LastError));
            Raise(nameof(LastErrorVisibility));
        }
    }

    private void Raise(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
