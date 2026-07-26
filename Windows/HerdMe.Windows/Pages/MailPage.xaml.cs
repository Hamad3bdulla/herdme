using System.Collections.ObjectModel;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace HerdMe.Windows.Pages;

public sealed partial class MailPage : Page
{
    private readonly MailCaptureService mail = AppServices.Mail;
    private readonly List<CapturedMail> allMessages = [];
    private bool previewConfigured;
    private Guid? previewMessageId;

    public ObservableCollection<CapturedMail> Messages { get; } = [];

    public MailPage()
    {
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        mail.MessageCaptured += Mail_MessageCaptured;
        Reload();
        UpdateServerState();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        mail.MessageCaptured -= Mail_MessageCaptured;
    }

    private void Mail_MessageCaptured(object? sender, CapturedMail message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            allMessages.Insert(0, message);
            ApplyFilter(message.MatchesSearch(SearchBox.Text) ? message.Id : null);
        });
    }

    private async void Server_Click(object sender, RoutedEventArgs e)
    {
        ServerButton.IsEnabled = false;
        try
        {
            if (mail.IsRunning) await mail.StopAsync();
            else await mail.StartAsync();
        }
        catch (Exception error)
        {
            await ShowErrorAsync(error.Message);
        }
        finally
        {
            ServerButton.IsEnabled = true;
            UpdateServerState();
        }
    }

    private void CopyEnvironment_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText("MAIL_MAILER=smtp\r\nMAIL_HOST=127.0.0.1\r\nMAIL_PORT=2525");
        Clipboard.SetContent(package);
    }

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowMessage(MessageList.SelectedItem as CapturedMail);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is not CapturedMail message) return;
        mail.Delete(message);
        allMessages.Remove(message);
        ApplyFilter();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        mail.Clear();
        allMessages.Clear();
        Messages.Clear();
        ShowMessage(null);
    }

    private void Reload()
    {
        allMessages.Clear();
        allMessages.AddRange(mail.Load());
        ApplyFilter();
    }

    private void ApplyFilter(Guid? preferredMessageId = null)
    {
        var selectedId = preferredMessageId ?? (MessageList.SelectedItem as CapturedMail)?.Id;
        Messages.Clear();
        foreach (var message in allMessages.Where(message => message.MatchesSearch(SearchBox.Text)))
        {
            Messages.Add(message);
        }
        MessageList.SelectedItem = Messages.FirstOrDefault(message => message.Id == selectedId)
            ?? Messages.FirstOrDefault();
        ShowMessage(MessageList.SelectedItem as CapturedMail);
    }

    private void ShowMessage(CapturedMail? message)
    {
        SubjectText.Text = message?.Subject ?? "Select a message";
        SenderText.Text = message is null ? string.Empty : "From: " + message.Sender;
        RecipientText.Text = message is null ? string.Empty : "To: " + message.RecipientsText;
        BodyText.Text = message?.Body ?? string.Empty;
        RawText.Text = message?.Raw ?? string.Empty;
        DeleteButton.IsEnabled = message is not null;
        _ = UpdatePreviewAsync(message);
    }

    private async Task UpdatePreviewAsync(CapturedMail? message)
    {
        MailPreviewFailureState.Visibility = Visibility.Collapsed;
        HtmlPreview.Visibility = Visibility.Visible;
        try
        {
            await HtmlPreview.EnsureCoreWebView2Async();
            if (!IsSelected(message)) return;
            ConfigurePreviewOnce();
            var html = message?.HtmlBody
                ?? $"<pre>{WebUtility.HtmlEncode(message?.Body ?? string.Empty)}</pre>";
            previewMessageId = message?.Id;
            HtmlPreview.NavigateToString(MailMimeParser.SafeHtmlDocument(html));
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
            await ReportPreviewFailureAsync("initialization", message, error.ToString());
            if (IsSelected(message)) ShowMailPreviewFailure();
        }
    }

    private async void HtmlPreview_NavigationCompleted(
        WebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args
    )
    {
        if (!IsCurrentPreviewSelection()) return;
        if (args.IsSuccess)
        {
            MailPreviewFailureState.Visibility = Visibility.Collapsed;
            HtmlPreview.Visibility = Visibility.Visible;
            return;
        }
        await ReportPreviewFailureAsync(
            "navigation",
            MessageList.SelectedItem as CapturedMail,
            $"WebView2 status: {args.WebErrorStatus}"
        );
        ShowMailPreviewFailure();
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

    private static void Preview_NavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs args
    )
    {
        if (!MailMimeParser.IsPreviewNavigationAllowed(args.Uri)) args.Cancel = true;
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

    private void UpdateServerState()
    {
        ServerStatusText.Text = mail.IsRunning ? $"Running on 127.0.0.1:{mail.Port}" : "Stopped";
        ServerButtonIcon.Symbol = mail.IsRunning ? Symbol.Stop : Symbol.Play;
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "HerdMe",
            Content = message,
            CloseButtonText = "OK"
        };
        await dialog.ShowAsync();
    }
}
