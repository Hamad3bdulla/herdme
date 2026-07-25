using System.Collections.ObjectModel;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using System.Net;
using System.Runtime.InteropServices;

namespace HerdMe.Windows.Pages;

public sealed partial class MailPage : Page
{
    private readonly MailCaptureService mail = AppServices.Mail;

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
            Messages.Insert(0, message);
            MessageList.SelectedItem = message;
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

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is not CapturedMail message) return;
        mail.Delete(message);
        Messages.Remove(message);
        MessageList.SelectedItem = Messages.FirstOrDefault();
        ShowMessage(MessageList.SelectedItem as CapturedMail);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        mail.Clear();
        Messages.Clear();
        ShowMessage(null);
    }

    private void Reload()
    {
        Messages.Clear();
        foreach (var message in mail.Load()) Messages.Add(message);
        MessageList.SelectedItem = Messages.FirstOrDefault();
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
        try
        {
            await HtmlPreview.EnsureCoreWebView2Async();
            HtmlPreview.CoreWebView2.Settings.IsScriptEnabled = false;
            HtmlPreview.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            HtmlPreview.CoreWebView2.Settings.IsWebMessageEnabled = false;
            HtmlPreview.CoreWebView2.Settings.AreDevToolsEnabled = false;
            var html = message?.HtmlBody
                ?? $"<pre style=\"white-space:pre-wrap\">{WebUtility.HtmlEncode(message?.Body ?? string.Empty)}</pre>";
            HtmlPreview.NavigateToString(MailMimeParser.SafeHtmlDocument(html));
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
        }
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
