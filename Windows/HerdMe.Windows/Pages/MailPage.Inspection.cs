using System.Text.RegularExpressions;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HerdMe.Windows.Pages;

// Headers and Links tabs plus the phone-width preview toggle.
public sealed partial class MailPage
{
    private const double MobilePreviewWidth = 375;

    private static (IReadOnlyList<MailHeader> Headers, IReadOnlyList<MailLink> Links) Inspect(CapturedMail message)
    {
        try
        {
            return (MailInspection.Headers(message.Raw), MailInspection.Links(message.HtmlBody));
        }
        catch (RegexMatchTimeoutException)
        {
            return (MailInspection.Headers(message.Raw), []);
        }
    }

    private void ClearInspection(bool hasMessage)
    {
        HeaderList.Children.Clear();
        LinkList.Children.Clear();
        LinkSummaryText.Text = string.Empty;
        LinksTab.Header = AppLocalization.Get("MailLinksTabPlain");
        MobileWidthToggle.IsEnabled = hasMessage;
    }

    private void RenderInspection(IReadOnlyList<MailHeader> headers, IReadOnlyList<MailLink> links)
    {
        HeaderList.Children.Clear();
        foreach (var header in headers)
        {
            var row = new Grid { ColumnSpacing = 12, FlowDirection = FlowDirection.LeftToRight };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = header.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = true
            });
            var value = new TextBlock
            {
                Text = CapturePreview.LimitText(header.Value, 4_096).Text,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12
            };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            HeaderList.Children.Add(row);
        }

        LinkList.Children.Clear();
        var problems = links.Count(link => link.Issue != MailLinkIssue.None);
        LinksTab.Header = problems > 0
            ? AppLocalization.Format("MailLinksTabProblems", problems)
            : AppLocalization.Get("MailLinksTabPlain");
        LinkSummaryText.Text = links.Count == 0
            ? AppLocalization.Get("MailLinksNone")
            : problems == 0
                ? AppLocalization.Format("MailLinksAllGood", links.Count)
                : AppLocalization.Format("MailLinksProblems", links.Count, problems);
        foreach (var link in links) LinkList.Children.Add(LinkRow(link));
    }

    private static UIElement LinkRow(MailLink link)
    {
        var ok = link.Issue == MailLinkIssue.None;
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon
        {
            Glyph = ok ? "\uE73E" : "\uE7BA",
            FontSize = 14,
            Style = StatusStyles.Glyph(ok ? StatusTone.Success : StatusTone.Caution),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        });
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = link.Url.Length == 0 ? AppLocalization.Get("MailLinkEmptyUrl") : link.Url,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FlowDirection = FlowDirection.LeftToRight,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12
        });
        var caption = string.Join("  -  ", new[] { link.Text, IssueText(link.Issue) }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        if (caption.Length > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = caption,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextStyle"]
            });
        }
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (link.Url.Length > 0)
        {
            var copy = new Button
            {
                Content = new SymbolIcon(Symbol.Copy),
                Style = (Style)Application.Current.Resources["ToolbarIconButtonStyle"],
                VerticalAlignment = VerticalAlignment.Top
            };
            var name = AppLocalization.Get("MailLinkCopy");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, name);
            ToolTipService.SetToolTip(copy, name);
            copy.Click += (_, _) =>
            {
                var package = new DataPackage();
                package.SetText(link.Url);
                Clipboard.SetContent(package);
                App.MainWindow.ShowToast(AppLocalization.Get("CommonCopiedToast"));
            };
            Grid.SetColumn(copy, 2);
            row.Children.Add(copy);
        }
        return row;
    }

    private static string IssueText(MailLinkIssue issue) => issue switch
    {
        MailLinkIssue.Empty => AppLocalization.Get("MailLinkIssueEmpty"),
        MailLinkIssue.Insecure => AppLocalization.Get("MailLinkIssueInsecure"),
        MailLinkIssue.Localhost => AppLocalization.Get("MailLinkIssueLocalhost"),
        MailLinkIssue.Relative => AppLocalization.Get("MailLinkIssueRelative"),
        _ => string.Empty
    };

    private void MobileWidth_Click(object sender, RoutedEventArgs e) => ApplyPreviewWidth();

    private void ApplyPreviewWidth()
    {
        var mobile = MobileWidthToggle.IsChecked == true;
        HtmlPreview.Width = mobile ? MobilePreviewWidth : double.NaN;
        HtmlPreview.HorizontalAlignment = mobile ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
    }
}
