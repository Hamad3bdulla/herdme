using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using HerdMe.Windows.Models;
using HerdMe.Windows.ViewModels;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace HerdMe.Windows.Pages;

// Hover preview on a Sites row: the last thumbnail of the live preview, plus the HTTP status
// and response time from one quick local request (cached for a few seconds).
public sealed partial class SitesPage
{
    private readonly Dictionary<string, (DateTimeOffset At, SiteProbeResult Result)> probeCache =
        new(StringComparer.OrdinalIgnoreCase);

    private async void SiteRowPreview_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip { Tag: string path } tip) return;
        var site = Sites.FirstOrDefault(candidate =>
            candidate.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (site is null) return;

        var status = new TextBlock
        {
            Text = AppLocalization.Get("SitesPreviewChecking"),
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            TextWrapping = TextWrapping.Wrap
        };
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Style = StatusStyles.Dot(StatusTone.Neutral),
            VerticalAlignment = VerticalAlignment.Center
        };
        var panel = new StackPanel { Spacing = 8, Width = 260 };
        panel.Children.Add(PreviewThumbnail(site));
        panel.Children.Add(new TextBlock
        {
            Text = site.Domain,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        statusRow.Children.Add(dot);
        statusRow.Children.Add(status);
        panel.Children.Add(statusRow);
        tip.Content = panel;

        if (!environment.IsRunning)
        {
            status.Text = AppLocalization.Get("SitesPreviewEnvironmentStopped");
            return;
        }
        var result = await ProbeCachedAsync(site);
        if (result is null || !tip.IsOpen) return;
        status.Text = SiteQuickProbe.Describe(result);
        dot.Style = StatusStyles.Dot(result.Tone switch
        {
            SiteProbeTone.Success => StatusTone.Success,
            SiteProbeTone.Caution => StatusTone.Caution,
            _ => StatusTone.Critical
        });
    }

    private async Task<SiteProbeResult?> ProbeCachedAsync(SiteRecord site)
    {
        if (probeCache.TryGetValue(site.Path, out var cached)
            && DateTimeOffset.UtcNow - cached.At < SiteQuickProbe.CacheLifetime) return cached.Result;
        try
        {
            var result = await SiteQuickProbe.ProbeAsync(SiteUri(site));
            probeCache[site.Path] = (DateTimeOffset.UtcNow, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private FrameworkElement PreviewThumbnail(SiteRecord site)
    {
        var frame = new Border
        {
            Height = 150,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["SitesTintBrush"]
        };
        var path = SiteQuickProbe.ThumbnailPath(settingsStore.SupportRoot, site.Path);
        if (File.Exists(path))
        {
            var image = new Image
            {
                Stretch = Stretch.UniformToFill,
                VerticalAlignment = VerticalAlignment.Top
            };
            frame.Child = image;
            _ = LoadThumbnailAsync(image, path);
            return frame;
        }
        // No thumbnail yet: the framework monogram, like the row tile.
        var monogram = FrameworkVisuals.Monogram(site.Framework);
        frame.Child = monogram.Length > 0
            ? new TextBlock
            {
                Text = monogram,
                FontSize = 40,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Style = FrameworkVisuals.MonogramStyle(site.Framework)
            }
            : new FontIcon
            {
                Glyph = "\uE774",
                FontSize = 36,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["BrandBrush"]
            };
        return frame;
    }

    // Read into memory so the file is never locked and a newer capture is never served stale.
    private static async Task LoadThumbnailAsync(Image image, string path)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 520 };
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException)
        {
        }
    }

    // Saves what the live preview shows, for the hover preview of this row.
    private async Task CaptureSiteThumbnailAsync(SiteRecord site)
    {
        if (SitePreview.CoreWebView2 is not { } webView) return;
        try
        {
            await Task.Delay(700);
            if (!loaded || !IsSelected(site) || SitePreview.CoreWebView2 is null) return;
            var path = SiteQuickProbe.ThumbnailPath(settingsStore.SupportRoot, site.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new InMemoryRandomAccessStream();
            await webView.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Seek(0);
            var temporary = path + ".tmp";
            await using (var file = File.Create(temporary))
            {
                await stream.AsStreamForRead().CopyToAsync(file);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or COMException or InvalidOperationException or ObjectDisposedException)
        {
            // The thumbnail is a nicety; the hover falls back to the monogram.
        }
    }
}
