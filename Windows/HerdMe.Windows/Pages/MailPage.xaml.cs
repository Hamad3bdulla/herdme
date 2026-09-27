using System.Collections.ObjectModel;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HerdMe.Windows.Pages;

public sealed partial class MailPage : Page
{
    private readonly MailCaptureService mail;
    private readonly CoreClient coreClient;
    private readonly SiteConfigurationStore siteSettings;
    private readonly List<CapturedMail> allMessages = [];
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CaptureRefreshSession<IReadOnlyList<CapturedMail>>? refreshSession;
    private CapturedMail? displayedMessage;
    private bool mutating;
    private bool clearPending;
    private bool exporting;
    private bool updatingList;
    private bool loadFailed;
    private bool loaded;
    private int pageGeneration;
    private int previewGeneration;
    private EventHandler<CapturedMail>? messageCapturedHandler;
    private bool previewConfigured;
    private Guid? previewMessageId;
    private ulong? previewNavigationId;

    public ObservableCollection<CapturedMail> Messages { get; } = [];

    public MailPage(
        MailCaptureService mail,
        CoreClient coreClient,
        SiteConfigurationStore siteSettings
    )
    {
        this.mail = mail;
        this.coreClient = coreClient;
        this.siteSettings = siteSettings;
        InitializeComponent();
        refreshTimer.Tick += async (_, _) => await ReloadAsync();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        loadFailed = false;
        CaptureErrorBar.IsOpen = false;
        pageGeneration++;
        var session = new CaptureRefreshSession<IReadOnlyList<CapturedMail>>(mail.LoadSummaries);
        refreshSession = session;
        messageCapturedHandler = (_, _) => session.RequestRefresh();
        mail.MessageCaptured += messageCapturedHandler;
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
        previewGeneration++;
        previewMessageId = null;
        previewNavigationId = null;
        HtmlPreview.Visibility = Visibility.Collapsed;
        MailPreviewFailureState.Visibility = Visibility.Collapsed;
        MailPreviewProgress.IsActive = false;
        MailPreviewProgress.Visibility = Visibility.Collapsed;
        mail.MessageCaptured -= messageCapturedHandler;
        messageCapturedHandler = null;
        allMessages.Clear();
        Messages.Clear();
        ShowMessage(null);
    }

    private async void Server_Click(object sender, RoutedEventArgs e)
    {
        var generation = pageGeneration;
        ServerButton.IsEnabled = false;
        try
        {
            if (mail.IsRunning) await mail.StopAsync();
            else await mail.StartAsync();
        }
        catch (Exception error)
        {
            if (loaded && generation == pageGeneration) await ShowErrorAsync(error.Message);
        }
        finally
        {
            ServerButton.IsEnabled = true;
            if (loaded) UpdateServerState();
        }
    }

    private async void AddToEnvironment_Click(object sender, RoutedEventArgs e)
    {
        var generation = pageGeneration;
        try
        {
            var settings = siteSettings.Load();
            var sites = await coreClient.ScanAsync(
                settings.Roots,
                settings.Tld,
                settings.LinkedSites
            );
            if (!loaded || generation != pageGeneration) return;
            if (sites.Count == 0)
            {
                await ShowErrorAsync(AppLocalization.Get("ServicesAddSiteBeforeEnvironment"));
                return;
            }

            var siteBox = new ComboBox
            {
                Header = AppLocalization.Get("ServicesSiteField"),
                ItemsSource = sites,
                DisplayMemberPath = nameof(SiteRecord.Name),
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var pathText = new TextBlock
            {
                Text = Path.Combine(sites[0].Path, ".env"),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            };
            siteBox.SelectionChanged += (_, _) =>
            {
                if (siteBox.SelectedItem is SiteRecord site)
                {
                    pathText.Text = Path.Combine(site.Path, ".env");
                }
            };
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(siteBox);
            content.Children.Add(pathText);
            if (XamlRoot is not { } xamlRoot) return;
            var dialog = new ContentDialog
            {
                FlowDirection = AppLocalization.LayoutDirection,
                XamlRoot = xamlRoot,
                Title = AppLocalization.Get("MailEnvironmentDialogTitle"),
                Content = content,
                PrimaryButtonText = AppLocalization.Get("ServicesAddToEnvironment"),
                CloseButtonText = AppLocalization.Get("CommonCancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || !loaded || generation != pageGeneration
                || siteBox.SelectedItem is not SiteRecord selectedSite)
            {
                return;
            }

            var update = ServiceEnvironmentFile.Update(
                selectedSite.Path,
                MailEnvironmentConfiguration.Variables(
                    mail.Port ?? MailCaptureService.DefaultPort
                ),
                "HerdMe Mail"
            );
            await ShowMessageAsync(
                AppLocalization.Get("ServicesEnvironmentUpdatedTitle"),
                AppLocalization.Format(
                    "MailEnvironmentUpdatedMessage",
                    update.AddedKeys,
                    update.UpdatedKeys,
                    selectedSite.Name
                )
            );
        }
        catch (Exception error)
        {
            if (loaded && generation == pageGeneration) await ShowErrorAsync(error.Message);
        }
    }

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingList) return;
        ShowMessage(MessageList.SelectedItem as CapturedMail);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!loaded) return;
        ApplyFilter();
    }

    private void SearchAccelerator_Invoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args
    )
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is not CapturedMail message) return;
        await MutateAsync(() => mail.Delete(message));
    }

    // Clear hides the inbox at once and deletes after the Undo countdown (no confirmation).
    // Only the messages that were hidden are deleted; mail that arrives meanwhile stays.
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (mutating || clearPending || !loaded || allMessages.Count == 0) return;
        var hidden = allMessages.ToArray();
        clearPending = true;
        refreshSession?.Invalidate();
        allMessages.Clear();
        ApplyFilter();
        UpdateCaptureState();
        App.MainWindow.RunDeferred(
            AppLocalization.Format("MailClearedPending", hidden.Length),
            async () =>
            {
                // Runs even if the page was left meanwhile; the page only refreshes if shown.
                try
                {
                    await Task.Run(() =>
                    {
                        foreach (var message in hidden) mail.Delete(message);
                    });
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
        if (exporting || MessageList.SelectedItem is not CapturedMail message) return;
        var generation = pageGeneration;
        exporting = true;
        UpdateCaptureState();
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"mail-{message.ReceivedAt:yyyyMMdd-HHmmss}-{message.Id:N}"
            };
            picker.FileTypeChoices.Add(AppLocalization.Get("MailExportFileType"), [".eml"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null || !loaded || generation != pageGeneration) return;
            await Task.Run(() => CaptureExport.SaveMailAsync(mail.Complete(message) ?? message, file.Path));
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

    private async Task MutateAsync(Action operation)
    {
        if (mutating || refreshSession is not { } session) return;
        mutating = true;
        session.Invalidate();
        UpdateCaptureState();
        try
        {
            await Task.Run(operation);
            if (loaded && ReferenceEquals(session, refreshSession)) CaptureErrorBar.IsOpen = false;
        }
        catch (Exception error)
        {
            if (loaded && ReferenceEquals(session, refreshSession))
                ShowCaptureError("CaptureOperationFailed", error);
        }
        finally
        {
            mutating = false;
            refreshSession?.Invalidate();
            if (loaded)
            {
                UpdateCaptureState();
                await ReloadAsync();
            }
        }
    }

    private async Task ReloadAsync()
    {
        if (!loaded || mutating || clearPending || refreshSession is not { } session
            || session.IsLoading || !session.NeedsRefresh) return;
        var loading = session.RefreshAsync();
        UpdateCaptureState();
        try
        {
            var messages = await loading;
            if (!loaded || !ReferenceEquals(session, refreshSession) || messages is null) return;
            if (loadFailed) CaptureErrorBar.IsOpen = false;
            loadFailed = false;
            var retained = allMessages.ToDictionary(message => message.Id);
            allMessages.Clear();
            allMessages.AddRange(messages.Select(message => retained.GetValueOrDefault(message.Id) ?? message));
            ApplyFilter();
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
        var busy = mutating || exporting || refreshSession?.IsLoading == true;
        CaptureProgress.IsActive = busy;
        CaptureProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = !mutating && allMessages.Count > 0;
        DeleteButton.IsEnabled = !mutating && MessageList.SelectedItem is CapturedMail;
        ExportButton.IsEnabled = !exporting && MessageList.SelectedItem is CapturedMail;
        EmptyState.Text = AppLocalization.Get(allMessages.Count == 0 ? "MailInboxEmpty" : "MailSearchEmpty");
        EmptyStatePanel.Visibility = Messages.Count == 0 && !busy && !loadFailed
            ? Visibility.Visible : Visibility.Collapsed;
        var inboxEmpty = allMessages.Count == 0;
        if (inboxEmpty) SmtpSnippetText.Text = SmtpSnippet();
        SmtpSetupPanel.Visibility = inboxEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyFilter()
    {
        var selectedId = (MessageList.SelectedItem as CapturedMail)?.Id;
        updatingList = true;
        try
        {
            CaptureListUpdater.Apply(Messages,
                allMessages.Where(message => message.MatchesSearch(SearchBox.Text)).ToArray(),
                message => message.Id);
            MessageList.SelectedItem = Messages.FirstOrDefault(message => message.Id == selectedId)
                ?? Messages.FirstOrDefault();
        }
        finally { updatingList = false; }
        ShowMessage(MessageList.SelectedItem as CapturedMail);
        UpdateCaptureState();
    }

    private void ShowMessage(CapturedMail? message)
    {
        if (ReferenceEquals(displayedMessage, message)) return;
        displayedMessage = message;
        SubjectText.Text = message is null ? AppLocalization.Get("MailSelectMessage")
            : CapturePreview.LimitText(message.Subject, 2_048).Text;
        SenderText.Text = message is null
            ? string.Empty
            : AppLocalization.Format("MailFrom", CapturePreview.LimitText(message.Sender, 2_048).Text);
        RecipientText.Text = message is null
            ? string.Empty
            : AppLocalization.Format("MailTo", CapturePreview.LimitText(message.RecipientsText, 4_096).Text);
        BodyText.Text = string.Empty;
        RawText.Text = string.Empty;
        ClearInspection(message is not null);
        PreviewSizeNotice.IsOpen = false;
        DeleteButton.IsEnabled = !mutating && message is not null;
        ExportButton.IsEnabled = !exporting && message is not null;
        _ = UpdatePreviewAsync(message);
    }

    private async Task UpdatePreviewAsync(CapturedMail? message)
    {
        if (!loaded) return;
        var generation = ++previewGeneration;
        previewNavigationId = null;
        MailPreviewFailureState.Visibility = Visibility.Collapsed;
        previewMessageId = message?.Id;
        HtmlPreview.Visibility = Visibility.Collapsed;
        MailPreviewProgress.IsActive = message is not null;
        MailPreviewProgress.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        if (message is null)
        {
            return;
        }
        try
        {
            var (preview, inspection) = await Task.Run(() =>
            {
                var complete = mail.Complete(message) ?? message;
                return (CapturePreview.ForMail(complete), Inspect(complete));
            });
            if (!loaded || generation != previewGeneration || !IsSelected(message)) return;
            BodyText.Text = preview.Body;
            RawText.Text = preview.Raw;
            RenderInspection(inspection.Headers, inspection.Links);
            ApplyPreviewWidth();
            PreviewSizeNotice.IsOpen = preview.IsTruncated;
            HtmlPreview.Visibility = Visibility.Visible;
            await HtmlPreview.EnsureCoreWebView2Async();
            if (!loaded || generation != previewGeneration || !IsSelected(message)) return;
            ConfigurePreviewOnce();
            HtmlPreview.NavigateToString(preview.HtmlDocument);
        }
        catch (Exception error) when (error is InvalidOperationException or COMException
            or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
        {
            if (!loaded || generation != previewGeneration) return;
            await ReportPreviewFailureAsync("initialization", message, error.ToString());
            if (loaded && generation == previewGeneration && IsSelected(message)) ShowMailPreviewFailure();
        }
        finally
        {
            if (loaded && generation == previewGeneration)
            {
                MailPreviewProgress.IsActive = false;
                MailPreviewProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async void HtmlPreview_NavigationCompleted(
        WebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args
    )
    {
        if (!loaded || !IsCurrentPreviewSelection()) return;
        if (args.NavigationId != previewNavigationId) return;
        var generation = previewGeneration;
        if (args.IsSuccess)
        {
            MailPreviewFailureState.Visibility = Visibility.Collapsed;
            HtmlPreview.Visibility = Visibility.Visible;
            return;
        }
        if (IsExpectedNavigationCancellation(args.WebErrorStatus)) return;
        await ReportPreviewFailureAsync(
            "navigation",
            MessageList.SelectedItem as CapturedMail,
            $"WebView2 status: {args.WebErrorStatus}"
        );
        if (loaded && generation == previewGeneration
            && args.NavigationId == previewNavigationId) ShowMailPreviewFailure();
    }

    private static bool IsExpectedNavigationCancellation(CoreWebView2WebErrorStatus status)
    {
        return status is CoreWebView2WebErrorStatus.OperationCanceled
            or CoreWebView2WebErrorStatus.ConnectionAborted;
    }

    private void RetryMailPreview_Click(object sender, RoutedEventArgs e)
    {
        _ = UpdatePreviewAsync(MessageList.SelectedItem as CapturedMail);
    }

    private void ShowMailPreviewFailure()
    {
        HtmlPreview.Visibility = Visibility.Collapsed;
        MailPreviewFailureState.Visibility = Visibility.Visible;
    }

    private bool IsSelected(CapturedMail? message)
    {
        return (MessageList.SelectedItem as CapturedMail)?.Id == message?.Id;
    }

    private bool IsCurrentPreviewSelection()
    {
        return (MessageList.SelectedItem as CapturedMail)?.Id == previewMessageId;
    }

    private static Task<bool> ReportPreviewFailureAsync(
        string stage,
        CapturedMail? message,
        string details
    )
    {
        return DiagnosticLog.WriteFailureAsync(
            "mail-preview",
            stage,
            message is null
                ? "The empty mail preview failed."
                : $"The preview for message {message.Id:D} failed.",
            details
        );
    }

    private void ConfigurePreviewOnce()
    {
        if (previewConfigured) return;
        var core = HtmlPreview.CoreWebView2;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.NavigationStarting += Preview_NavigationStarting;
        core.NewWindowRequested += Preview_NewWindowRequested;
        core.DownloadStarting += Preview_DownloadStarting;
        previewConfigured = true;
    }

    private void Preview_NavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs args
    )
    {
        if (!loaded || !MailMimeParser.IsPreviewNavigationAllowed(args.Uri))
        {
            args.Cancel = true;
            return;
        }
        previewNavigationId = args.NavigationId;
    }

    private static void Preview_NewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args
    )
    {
        args.Handled = true;
    }

    private static void Preview_DownloadStarting(
        CoreWebView2 sender,
        CoreWebView2DownloadStartingEventArgs args
    )
    {
        args.Cancel = true;
    }

    // The same lines "Add to .env" writes; selectable so they can also be copied by hand.
    private string SmtpSnippet() => string.Join(
        "\n",
        MailEnvironmentConfiguration.Variables(mail.Port ?? MailCaptureService.DefaultPort)
            .Select(variable => variable.Key + "=" + variable.Value)
    );

    private void UpdateServerState()
    {
        ServerStatusText.Text = mail.IsRunning
            ? AppLocalization.Format("MailRunningOn", mail.Port)
            : AppLocalization.Get("MailStopped");
        if (SmtpSetupPanel.Visibility == Visibility.Visible) SmtpSnippetText.Text = SmtpSnippet();
        ServerButtonIcon.Symbol = mail.IsRunning ? Symbol.Stop : Symbol.Play;
        ServerStatusDot.Style = Views.StatusStyles.Dot(
            mail.IsRunning ? Views.StatusTone.Success : Views.StatusTone.Neutral
        );
    }

    private async Task ShowErrorAsync(string message)
    {
        if (!loaded || XamlRoot is not { } xamlRoot) return;
        await ErrorDialog.ShowAsync(xamlRoot, message);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (!loaded || XamlRoot is not { } xamlRoot) return;
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = xamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = AppLocalization.Get("CommonOk")
        };
        await dialog.ShowAsync();
    }
}
