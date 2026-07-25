using System.Collections.ObjectModel;
using System.Diagnostics;
using HerdMe.Windows.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

public sealed partial class LogsPage : Page
{
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string currentContent = string.Empty;

    public ObservableCollection<LogFileRecord> Logs { get; } = [];

    private string LogRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HerdMe",
        "Log"
    );

    public LogsPage()
    {
        InitializeComponent();
        refreshTimer.Tick += RefreshTimer_Tick;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        Reload(force: true);
        refreshTimer.Start();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        refreshTimer.Stop();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Reload(force: true);
    }

    private void RefreshTimer_Tick(object? sender, object e)
    {
        if (LiveRefreshToggle.IsOn) Reload(force: false);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplySearch();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LogRoot);
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add(LogRoot);
        Process.Start(startInfo);
    }

    private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogList.SelectedItem is not LogFileRecord log)
        {
            LogTitleText.Text = "Select a log";
            currentContent = string.Empty;
            LogContentText.Text = string.Empty;
            return;
        }
        LogTitleText.Text = log.Name;
        LoadSelectedContent(log);
    }

    private void LoadSelectedContent(LogFileRecord log)
    {
        try
        {
            const int maximumBytes = 4 * 1_024 * 1_024;
            using var stream = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > maximumBytes) stream.Seek(-maximumBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            currentContent = reader.ReadToEnd();
        }
        catch (IOException error)
        {
            currentContent = error.Message;
        }
        ApplySearch();
    }

    private void ApplySearch()
    {
        LogContentText.Text = LogPresentation.FilterLines(currentContent, SearchBox.Text);
    }

    private void Reload(bool force)
    {
        Directory.CreateDirectory(LogRoot);
        var selectedPath = (LogList.SelectedItem as LogFileRecord)?.Path;
        var discovered = Directory.EnumerateFiles(LogRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
        {
            var info = new FileInfo(path);
            return new LogFileRecord
            {
                Name = Path.GetRelativePath(LogRoot, path),
                Path = path,
                Size = info.Length,
                ModifiedAt = info.LastWriteTimeUtc
            };
        }).ToList();
        var changed = force
            || discovered.Count != Logs.Count
            || discovered.Where((record, index) => !SameRecord(record, Logs[index])).Any();
        if (!changed) return;

        Logs.Clear();
        foreach (var record in discovered) Logs.Add(record);
        LogList.SelectedItem = Logs.FirstOrDefault(log => log.Path.Equals(
            selectedPath,
            StringComparison.OrdinalIgnoreCase
        )) ?? Logs.FirstOrDefault();
        if (LogList.SelectedItem is LogFileRecord selected)
        {
            LoadSelectedContent(selected);
        }
        else
        {
            LogTitleText.Text = "Select a log";
            currentContent = string.Empty;
            ApplySearch();
        }
    }

    private static bool SameRecord(LogFileRecord left, LogFileRecord right)
    {
        return left.Path.Equals(right.Path, StringComparison.OrdinalIgnoreCase)
            && left.Size == right.Size
            && left.ModifiedAt == right.ModifiedAt;
    }
}
