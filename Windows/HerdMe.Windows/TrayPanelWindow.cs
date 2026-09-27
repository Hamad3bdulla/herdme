using System.Runtime.InteropServices;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace HerdMe.Windows;

// What the tray quick panel shows. Built by App each time the panel opens or refreshes.
internal sealed record TrayPanelContent(
    bool Running,
    bool Degraded,
    IReadOnlyList<SiteRecord> Sites,
    int UnseenMail,
    int Shares
);

/// <summary>
/// The small window a left click on the tray icon opens: environment status, Start all /
/// Stop all, the favorite and recent sites, unseen mail and "Stop sharing". It sits against
/// the taskbar next to the icon (Services/TrayPanelPlacement.cs), never shows in Alt+Tab, and
/// hides as soon as it loses focus or Esc is pressed. The right-click menu is unchanged.
/// </summary>
internal sealed class TrayPanelWindow : Window
{
    private readonly App owner;
    private readonly Grid root;
    private readonly Border statusPill;
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse statusDot;
    private readonly TextBlock statusText;
    private readonly Button startButton;
    private readonly Button stopButton;
    private readonly StackPanel siteList;
    private readonly TextBlock sitesEmpty;
    private readonly Button mailButton;
    private readonly Button stopSharingButton;
    private string? signature;
    private bool closing;

    public TrayPanelWindow(App owner)
    {
        this.owner = owner;
        Title = "HerdMe";
        root = new Grid
        {
            Padding = new Thickness(16),
            RowSpacing = 12,
            FlowDirection = AppLocalization.LayoutDirection,
            Language = AppLocalization.LanguageTag
        };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
        {
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        }

        // Row 0: name and status.
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock
        {
            Text = "HerdMe",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetHeadingLevel(name, AutomationHeadingLevel.Level1);
        header.Children.Add(name);
        statusDot = new Microsoft.UI.Xaml.Shapes.Ellipse { Style = StatusStyles.Dot(StatusTone.Neutral) };
        statusText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var pillContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        pillContent.Children.Add(statusDot);
        pillContent.Children.Add(statusText);
        statusPill = new Border { Style = StatusStyles.Pill(StatusTone.Neutral), Child = pillContent };
        AutomationProperties.SetLiveSetting(statusPill, AutomationLiveSetting.Polite);
        Grid.SetColumn(statusPill, 1);
        header.Children.Add(statusPill);
        root.Children.Add(header);

        // Row 1: Start all / Stop all.
        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        startButton = IconButton("\uE768", AppLocalization.Get("TrayStartAllCommandLabel"));
        startButton.Click += async (_, _) =>
        {
            HidePanel();
            await owner.StartAllAsync();
        };
        stopButton = IconButton("\uE71A", AppLocalization.Get("TrayStopAllCommandLabel"));
        stopButton.Click += async (_, _) =>
        {
            HidePanel();
            await owner.StopAllAsync();
        };
        Grid.SetColumn(stopButton, 1);
        controls.Children.Add(startButton);
        controls.Children.Add(stopButton);
        Grid.SetRow(controls, 1);
        root.Children.Add(controls);

        // Rows 2-3: sites.
        var sitesHeader = new TextBlock
        {
            Text = AppLocalization.Get("TrayPanelSitesHeader"),
            Style = (Style)Application.Current.Resources["CaptionTextStyle"]
        };
        AutomationProperties.SetHeadingLevel(sitesHeader, AutomationHeadingLevel.Level2);
        Grid.SetRow(sitesHeader, 2);
        root.Children.Add(sitesHeader);
        siteList = new StackPanel { Spacing = 2 };
        AutomationProperties.SetName(siteList, AppLocalization.Get("TrayPanelSitesHeader"));
        AutomationProperties.SetAutomationId(siteList, "TrayPanelSites");
        sitesEmpty = new TextBlock
        {
            Text = AppLocalization.Get("TrayPanelSitesEmpty"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            Visibility = Visibility.Collapsed
        };
        var sitesArea = new StackPanel { Spacing = 6 };
        sitesArea.Children.Add(siteList);
        sitesArea.Children.Add(sitesEmpty);
        var allSites = new HyperlinkButton { Content = AppLocalization.Get("TraySitesShowAll") };
        allSites.Click += (_, _) => Run(() => owner.ShowPageFromPanel("sites"));
        sitesArea.Children.Add(allSites);
        var scroller = new ScrollViewer
        {
            Content = sitesArea,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroller, 3);
        root.Children.Add(scroller);

        // Row 4: mail and sharing.
        var extras = new StackPanel { Spacing = 6 };
        mailButton = IconButton("\uE715", AppLocalization.Get("TrayMailLabel"));
        mailButton.Click += (_, _) => Run(() => owner.ShowPageFromPanel("mail"));
        stopSharingButton = IconButton("\uE72D", AppLocalization.Get("TrayStopSharingLabel"));
        stopSharingButton.Visibility = Visibility.Collapsed;
        stopSharingButton.Click += (_, _) => Run(owner.StopSharingFromPanel);
        extras.Children.Add(mailButton);
        extras.Children.Add(stopSharingButton);
        Grid.SetRow(extras, 4);
        root.Children.Add(extras);

        // Row 5: open the full window.
        var open = IconButton("\uE8A7", AppLocalization.Get("TrayOpenCommandLabel"));
        open.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        open.Click += (_, _) => Run(owner.ShowMainWindowFromPanel);
        Grid.SetRow(open, 5);
        root.Children.Add(open);

        root.KeyDown += Root_KeyDown;
        Content = root;

        if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        else
        {
            root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        }

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += AppWindow_Closing;
        Activated += TrayPanelWindow_Activated;
    }

    public bool IsOpen { get; private set; }

    // When the panel last hid. A click on the tray icon first takes focus away from the panel
    // (which hides it); without this the same click would open it again right away.
    public DateTimeOffset HiddenAt { get; private set; } = DateTimeOffset.MinValue;

    public void ShowNear(TrayPanelContent content)
    {
        if (closing) return;
        Refresh(content);
        var handle = WindowNative.GetWindowHandle(this);
        (int X, int Y)? anchor = GetCursorPos(out var point) ? (point.X, point.Y) : null;
        var area = anchor is { } at
            ? DisplayArea.GetFromPoint(new PointInt32(at.X, at.Y), DisplayAreaFallback.Nearest)
            : DisplayArea.Primary;
        var scale = MonitorScale(anchor);
        var bounds = TrayPanelPlacement.Place(
            ToBounds(area.OuterBounds),
            ToBounds(area.WorkArea),
            anchor,
            TrayPanelPlacement.Scale(TrayPanelPlacement.LogicalWidth, scale),
            TrayPanelPlacement.Scale(TrayPanelPlacement.LogicalHeight, scale),
            TrayPanelPlacement.Scale(TrayPanelPlacement.LogicalMargin, scale),
            AppLocalization.LayoutDirection == FlowDirection.RightToLeft
        );
        AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        AppWindow.Show();
        Activate();
        // The tray click came from Explorer, so the panel has to ask for the foreground.
        SetForegroundWindow(handle);
        IsOpen = true;
        (startButton.IsEnabled ? startButton : stopButton).Focus(FocusState.Programmatic);
    }

    // Rebuilds only what changed, so a pointer resting on a site is not disturbed.
    public void Refresh(TrayPanelContent content)
    {
        if (closing) return;
        var tone = content.Running
            ? StatusTone.Success
            : content.Degraded ? StatusTone.Caution : StatusTone.Critical;
        statusPill.Style = StatusStyles.Pill(tone);
        statusDot.Style = StatusStyles.Dot(tone);
        statusText.Text = AppLocalization.Get(
            content.Running ? "DashboardRunning" : content.Degraded ? "DashboardRecovering" : "DashboardStopped"
        );
        AutomationProperties.SetName(statusPill, AppLocalization.Format("TitleBarEnvironmentStatus", statusText.Text));
        startButton.IsEnabled = !content.Running;
        stopButton.IsEnabled = content.Running || content.Degraded;
        SetLabel(mailButton, content.UnseenMail > 0
            ? AppLocalization.Format("TrayMailUnseenLabel", content.UnseenMail)
            : AppLocalization.Get("TrayMailLabel"));
        stopSharingButton.Visibility = content.Shares > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (content.Shares > 0) SetLabel(stopSharingButton, AppLocalization.Format("TrayStopSharingLabel", content.Shares));

        var sites = TrayPresentation.MenuSites(content.Sites);
        var next = string.Join("\n", sites.Select(site => site.Path + "|" + site.Domain + "|" + site.IsFavorite))
            + "|" + content.Running;
        if (string.Equals(next, signature, StringComparison.Ordinal)) return;
        signature = next;
        siteList.Children.Clear();
        foreach (var site in sites) siteList.Children.Add(SiteRow(site, content.Running));
        sitesEmpty.Visibility = sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void HidePanel()
    {
        if (!IsOpen) return;
        IsOpen = false;
        HiddenAt = DateTimeOffset.UtcNow;
        AppWindow.Hide();
    }

    // Only App closes the panel for good, when HerdMe quits.
    public void ClosePanel()
    {
        if (closing) return;
        closing = true;
        Activated -= TrayPanelWindow_Activated;
        AppWindow.Closing -= AppWindow_Closing;
        root.KeyDown -= Root_KeyDown;
        IsOpen = false;
        Close();
    }

    private Grid SiteRow(SiteRecord site, bool running)
    {
        var path = site.Path;
        var row = new Grid { ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(new FontIcon { Glyph = site.IsFavorite ? "\uE735" : "\uE774", FontSize = 14 });
        label.Children.Add(new TextBlock { Text = site.Domain, TextTrimming = TextTrimming.CharacterEllipsis });
        var open = new Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            IsEnabled = running
        };
        var openName = AppLocalization.Format("TrayPanelOpenSite", site.Domain);
        AutomationProperties.SetName(open, openName);
        ToolTipService.SetToolTip(open, running ? openName : AppLocalization.Get("TrayPanelStartFirst"));
        open.Click += (_, _) => Run(() => owner.OpenSiteFromTray(path));
        var show = new Button
        {
            Content = new FontIcon { Glyph = "\uE8A7", FontSize = 14 },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0)
        };
        var showName = AppLocalization.Format("TrayPanelShowSite", site.Domain);
        AutomationProperties.SetName(show, showName);
        ToolTipService.SetToolTip(show, AppLocalization.Get("TraySiteShowInHerdMe"));
        show.Click += (_, _) => Run(() => owner.ShowSiteFromPanel(path));
        Grid.SetColumn(show, 1);
        row.Children.Add(open);
        row.Children.Add(show);
        return row;
    }

    private static Button IconButton(string glyph, string text)
    {
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch };
        SetLabel(button, text, glyph);
        return button;
    }

    private static void SetLabel(Button button, string text, string? glyph = null)
    {
        glyph ??= (button.Tag as string) ?? string.Empty;
        button.Tag = glyph;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = text });
        button.Content = content;
        AutomationProperties.SetName(button, text);
    }

    private void Run(Action action)
    {
        HidePanel();
        action();
    }

    private void TrayPanelWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) HidePanel();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape) return;
        args.Handled = true;
        HidePanel();
    }

    // Alt+F4 only hides the panel.
    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closing) return;
        args.Cancel = true;
        HidePanel();
    }

    private static PanelBounds ToBounds(RectInt32 rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    // The panel may open on another monitor than it last closed on, so the size follows the
    // DPI of the monitor that was clicked rather than the window's current one.
    private double MonitorScale((int X, int Y)? anchor)
    {
        uint dpi = 0;
        if (anchor is { } at)
        {
            var monitor = MonitorFromPoint(new NativePoint { X = at.X, Y = at.Y }, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, EffectiveDpi, out var dpiX, out _) >= 0) dpi = dpiX;
        }
        if (dpi == 0) dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        return dpi == 0 ? 1d : dpi / 96d;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const int EffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
