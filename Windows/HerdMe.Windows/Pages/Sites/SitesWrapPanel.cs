using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace HerdMe.Windows.Pages;

/// <summary>
/// Minimal wrapping row for dialog action buttons. Buttons flow onto the next line
/// instead of being clipped when labels grow (Arabic, 150% text scaling, narrow
/// dialogs). Right-to-left mirroring is applied by the framework after arrange.
/// </summary>
public sealed partial class SitesWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 8;

    public double VerticalSpacing { get; set; } = 8;

    public static SitesWrapPanel Of(params UIElement[] children)
    {
        var panel = new SitesWrapPanel();
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;
        double lineWidth = 0, lineHeight = 0, width = 0, height = 0;
        var lineHasItems = false;
        foreach (var child in Children)
        {
            child.Measure(new Size(limit, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (size.Width <= 0 && size.Height <= 0) continue;
            var next = lineHasItems ? lineWidth + HorizontalSpacing + size.Width : size.Width;
            if (lineHasItems && next > limit)
            {
                width = Math.Max(width, lineWidth);
                height += lineHeight + VerticalSpacing;
                lineWidth = size.Width;
                lineHeight = size.Height;
            }
            else
            {
                lineWidth = next;
                lineHeight = Math.Max(lineHeight, size.Height);
            }
            lineHasItems = true;
        }
        width = Math.Max(width, lineWidth);
        height += lineHeight;
        return new Size(double.IsInfinity(limit) ? width : Math.Min(width, limit), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        var lineHasItems = false;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (size.Width <= 0 && size.Height <= 0)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }
            if (lineHasItems && x + HorizontalSpacing + size.Width > finalSize.Width)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
                lineHasItems = false;
            }
            if (lineHasItems) x += HorizontalSpacing;
            var width = Math.Min(size.Width, finalSize.Width);
            child.Arrange(new Rect(x, y, width, size.Height));
            x += width;
            lineHeight = Math.Max(lineHeight, size.Height);
            lineHasItems = true;
        }
        return finalSize;
    }
}
