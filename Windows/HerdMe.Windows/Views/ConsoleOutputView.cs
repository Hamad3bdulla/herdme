using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace HerdMe.Windows.Views;

// Read-only command output with ANSI colours. Selectable, bounded, and it keeps following
// the end while the reader has not scrolled up.
public sealed class ConsoleOutputView
{
    public const int MaximumCharacters = 1 * 1_024 * 1_024;
    private const int MaximumSpans = 5_000;
    private const double BottomTolerance = 24;
    private readonly TextBlock text;
    private readonly Dictionary<AnsiColor, TextHighlighter> highlighters = [];
    private readonly List<AnsiSpan> spans = [];
    private AnsiParser parser = new();
    private string content = string.Empty;

    public ConsoleOutputView(double height)
    {
        text = new TextBlock
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
            FlowDirection = FlowDirection.LeftToRight,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12
        };
        Scroll = new ScrollViewer
        {
            Content = text,
            Height = height,
            Padding = new Thickness(10, 8, 10, 8),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(Scroll, "CommandConsoleOutput");
    }

    public ScrollViewer Scroll { get; }

    // Plain text (escape codes removed). Setting it replaces the output.
    public string Text
    {
        get => content;
        set
        {
            parser = new AnsiParser();
            content = string.Empty;
            spans.Clear();
            Append(value ?? string.Empty);
        }
    }

    public void Append(string value)
    {
        if (value.Length == 0 && content.Length > 0) return;
        var atBottom = Scroll.ScrollableHeight <= 0
            || Scroll.VerticalOffset >= Scroll.ScrollableHeight - BottomTolerance;
        var (plain, added) = parser.Append(value);
        var offset = content.Length;
        content += plain;
        foreach (var span in added) spans.Add(span with { Start = span.Start + offset });
        if (content.Length > MaximumCharacters)
        {
            var cut = content.Length - MaximumCharacters;
            content = content[cut..];
            for (var index = spans.Count - 1; index >= 0; index--)
            {
                var span = spans[index];
                var end = span.Start + span.Length - cut;
                if (end <= 0) spans.RemoveAt(index);
                else spans[index] = new AnsiSpan(Math.Max(0, span.Start - cut), end - Math.Max(0, span.Start - cut), span.Color);
            }
        }
        if (spans.Count > MaximumSpans) spans.RemoveRange(0, spans.Count - MaximumSpans);
        text.Text = content;
        Render();
        if (!atBottom) return;
        Scroll.UpdateLayout();
        Scroll.ChangeView(null, Scroll.ScrollableHeight, null, true);
    }

    private void Render()
    {
        text.TextHighlighters.Clear();
        foreach (var highlighter in highlighters.Values) highlighter.Ranges.Clear();
        foreach (var span in spans)
        {
            if (!highlighters.TryGetValue(span.Color, out var highlighter))
            {
                highlighter = new TextHighlighter
                {
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Foreground = BrushFor(span.Color)
                };
                highlighters[span.Color] = highlighter;
            }
            highlighter.Ranges.Add(new TextRange { StartIndex = span.Start, Length = span.Length });
        }
        foreach (var highlighter in highlighters.Values)
        {
            if (highlighter.Ranges.Count > 0) text.TextHighlighters.Add(highlighter);
        }
    }

    // Theme brushes rather than raw ANSI colours, so output stays readable in light, dark
    // and high-contrast themes.
    private static Brush BrushFor(AnsiColor color) => (Brush)Application.Current.Resources[color switch
    {
        AnsiColor.Red => "SystemFillColorCriticalBrush",
        AnsiColor.Yellow => "SystemFillColorCautionBrush",
        AnsiColor.Green => "SystemFillColorSuccessBrush",
        AnsiColor.Blue or AnsiColor.Cyan => "AccentTextFillColorPrimaryBrush",
        AnsiColor.Magenta => "AccentTextFillColorSecondaryBrush",
        AnsiColor.Black or AnsiColor.Gray => "TextFillColorSecondaryBrush",
        _ => "TextFillColorPrimaryBrush"
    }];
}
