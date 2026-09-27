using System.Collections.ObjectModel;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HerdMe.Windows.Pages;

public sealed partial class DumpsPage : Page
{
    private readonly DumpCaptureService capture;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CaptureRefreshSession<IReadOnlyList<CapturedDump>>? refreshSession;
    private CapturedDump? displayedDump;
    private bool clearPending;
    private bool exporting;
    private bool updatingList;
    private bool loadFailed;
    private bool loaded;
    private int pageGeneration;
    private EventHandler<CapturedDump>? dumpCapturedHandler;

    public ObservableCollection<CapturedDump> Dumps { get; } = [];

    public DumpsPage(DumpCaptureService capture)
    {
        this.capture = capture;
        InitializeComponent();
        refreshTimer.Tick += async (_, _) => await ReloadAsync();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        loadFailed = false;
        CaptureErrorBar.IsOpen = false;
        ApplyDumpView();
        pageGeneration++;
        var session = new CaptureRefreshSession<IReadOnlyList<CapturedDump>>(capture.LoadSummaries);
        refreshSession = session;
        dumpCapturedHandler = (_, _) => session.RequestRefresh();
        capture.DumpCaptured += dumpCapturedHandler;
        App.MainWindowVisibilityChanged -= App_MainWindowVisibilityChanged;
        App.MainWindowVisibilityChanged += App_MainWindowVisibilityChanged;
        // Polling pauses while the window is hidden to the tray or minimized.
        if (App.IsMainWindowVisible) refreshTimer.Start();
        UpdateServerState();
        await ReloadAsync();
    }

    private async void App_MainWindowVisibilityChanged(object? sender, bool visible)
    {
        if (!loaded) return;
        if (!visible)
        {
            refreshTimer.Stop();
            return;
        }
        refreshSession?.RequestRefresh();
        refreshTimer.Start();
        UpdateServerState();
        await ReloadAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        loaded = false;
        App.MainWindowVisibilityChanged -= App_MainWindowVisibilityChanged;
        refreshTimer.Stop();
        refreshSession?.Dispose();
        refreshSession = null;
        pageGeneration++;
        capture.DumpCaptured -= dumpCapturedHandler;
        dumpCapturedHandler = null;
        Dumps.Clear();
        ShowDump(null);
    }

    private async void Server_Click(object sender, RoutedEventArgs e)
    {
        var generation = pageGeneration;
        ServerButton.IsEnabled = false;
        try
        {
            if (capture.IsRunning) await capture.StopAsync();
            else await capture.StartAsync();
        }
        catch (Exception error)
        {
            if (!loaded || generation != pageGeneration || XamlRoot is not { } xamlRoot) return;
            var dialog = new ContentDialog
            {
                FlowDirection = AppLocalization.LayoutDirection,
                XamlRoot = xamlRoot,
                Title = "HerdMe",
                Content = error.Message,
                CloseButtonText = AppLocalization.Get("CommonOk")
            };
            await dialog.ShowAsync();
        }
        finally
        {
            ServerButton.IsEnabled = true;
            if (loaded) UpdateServerState();
        }
    }

    private void DumpList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingList) return;
        ShowDump(DumpList.SelectedItem as CapturedDump);
    }

    // Clear hides the list at once and deletes after the Undo countdown (no confirmation).
    // Only the dumps that were hidden are deleted; dumps that arrive meanwhile stay.
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (clearPending || !loaded || Dumps.Count == 0) return;
        var hidden = Dumps.ToArray();
        clearPending = true;
        refreshSession?.Invalidate();
        updatingList = true;
        try { Dumps.Clear(); }
        finally { updatingList = false; }
        ShowDump(null);
        UpdateCaptureState();
        App.MainWindow.RunDeferred(
            AppLocalization.Format("DumpsClearedPending", hidden.Length),
            async () =>
            {
                // Runs even if the page was left meanwhile; the page only refreshes if shown.
                try
                {
                    await Task.Run(() =>
                    {
                        foreach (var dump in hidden) capture.Delete(dump);
                    });
                    if (loaded) CaptureErrorBar.IsOpen = false;
                }
                catch (Exception error)
                {
                    if (loaded) ShowCaptureError("CaptureOperationFailed", error);
                }
                finally
                {
                    clearPending = false;
                    refreshSession?.RequestRefresh();
                    if (loaded) await ReloadAsync();
                }
            },
            async () =>
            {
                clearPending = false;
                refreshSession?.RequestRefresh();
                await ReloadAsync();
            }
        );
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (exporting || DumpList.SelectedItem is not CapturedDump dump) return;
        var generation = pageGeneration;
        exporting = true;
        UpdateCaptureState();
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"dump-{dump.ReceivedAt:yyyyMMdd-HHmmss}-{dump.Id:N}"
            };
            picker.FileTypeChoices.Add("JSON", [".json"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null || !loaded || generation != pageGeneration) return;
            await Task.Run(() => CaptureExport.SaveDumpAsync(capture.Complete(dump) ?? dump, file.Path));
        }
        catch (Exception error)
        {
            if (loaded && generation == pageGeneration) ShowCaptureError("CaptureExportFailed", error);
        }
        finally
        {
            exporting = false;
            if (loaded) UpdateCaptureState();
        }
    }

    private async Task ReloadAsync()
    {
        if (!loaded || clearPending || refreshSession is not { } session
            || session.IsLoading || !session.NeedsRefresh) return;
        var loading = session.RefreshAsync();
        UpdateCaptureState();
        try
        {
            var dumps = await loading;
            if (!loaded || !ReferenceEquals(session, refreshSession) || dumps is null) return;
            if (loadFailed) CaptureErrorBar.IsOpen = false;
            loadFailed = false;
            var selectedId = (DumpList.SelectedItem as CapturedDump)?.Id;
            updatingList = true;
            try
            {
                CaptureListUpdater.Apply(Dumps, dumps, dump => dump.Id);
                DumpList.SelectedItem = Dumps.FirstOrDefault(dump => dump.Id == selectedId)
                    ?? Dumps.FirstOrDefault();
            }
            finally { updatingList = false; }
            ShowDump(DumpList.SelectedItem as CapturedDump);
        }
        catch (Exception error)
        {
            if (!loaded || !ReferenceEquals(session, refreshSession)) return;
            loadFailed = true;
            ShowCaptureError("CaptureLoadFailed", error);
        }
        finally
        {
            if (loaded && ReferenceEquals(session, refreshSession)) UpdateCaptureState();
        }
    }

    private async void RetryLoad_Click(object sender, RoutedEventArgs e)
    {
        CaptureErrorBar.IsOpen = false;
        refreshSession?.RequestRefresh();
        await ReloadAsync();
    }

    private void ShowCaptureError(string titleKey, Exception error)
    {
        CaptureErrorBar.Title = AppLocalization.Get(titleKey);
        CaptureErrorBar.Message = error.Message;
        CaptureErrorBar.IsOpen = true;
    }

    private void UpdateCaptureState()
    {
        var busy = clearPending || exporting || refreshSession?.IsLoading == true;
        CaptureProgress.IsActive = busy;
        CaptureProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = !clearPending && Dumps.Count > 0;
        ExportButton.IsEnabled = !exporting && DumpList.SelectedItem is CapturedDump;
        EmptyState.Visibility = Dumps.Count == 0 && !busy && !loadFailed
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowDump(CapturedDump? dump)
    {
        if (ReferenceEquals(displayedDump, dump)) return;
        displayedDump = dump;
        SourceText.Text = dump is null ? AppLocalization.Get("DumpsSelectDump")
            : CapturePreview.LimitText(dump.Source, 2_048).Text;
        var preview = CapturePreview.LimitText(dump?.Summary ?? string.Empty);
        SummaryText.Text = preview.Text;
        PreviewSizeNotice.IsOpen = preview.IsTruncated;
        ExportButton.IsEnabled = !exporting && dump is not null;
        displayedDumpForTree = dump;
        ShowDumpTree(dump);
        if (dump is { IsSummary: true }) _ = ShowCompleteDumpAsync(dump);
    }

    private async Task ShowCompleteDumpAsync(CapturedDump row)
    {
        try
        {
            var complete = await Task.Run(() => capture.Complete(row));
            if (!loaded || !ReferenceEquals(displayedDump, row) || complete is null) return;
            SourceText.Text = CapturePreview.LimitText(complete.Source, 2_048).Text;
            var preview = CapturePreview.LimitText(complete.Summary);
            SummaryText.Text = preview.Text;
            PreviewSizeNotice.IsOpen = preview.IsTruncated;
            displayedDumpForTree = complete;
            ShowDumpTree(complete);
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException
            or System.Text.Json.JsonException or IOException)
        {
            if (loaded && ReferenceEquals(displayedDump, row)) ShowCaptureError("CaptureLoadFailed", error);
        }
    }

    private void UpdateServerState()
    {
        ServerStatusText.Text = capture.IsRunning
            ? AppLocalization.Format("DumpsRunningOn", capture.Port)
            : AppLocalization.Get("DumpsStopped");
        ServerButtonIcon.Symbol = capture.IsRunning ? Symbol.Stop : Symbol.Play;
        ServerStatusDot.Style = Views.StatusStyles.Dot(
            capture.IsRunning ? Views.StatusTone.Success : Views.StatusTone.Neutral
        );
    }
}
