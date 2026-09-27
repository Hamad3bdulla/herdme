using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace HerdMe.Windows.Pages;

// Coloured error/warning/debug lines, F8 / Shift+F8 between errors, and Follow tail.
// Colouring uses TextHighlighters so the log stays one selectable TextBlock.
public sealed partial class LogsPage
{
    private IReadOnlyList<LogLineSpan> errorSpans = [];
    private int currentError = -1;
    private int displayedLineCount = 1;
    private bool programmaticScroll;
    private TextHighlighter? currentErrorHighlighter;

    private bool FollowTail => FollowTailToggle.IsChecked == true;

    private void ApplyHighlights(string text)
    {
        LogContentText.TextHighlighters.Clear();
        currentErrorHighlighter = null;
        IReadOnlyList<LogLineSpan> spans = text.Length == 0 ? [] : LogHighlighting.Scan(text);
        displayedLineCount = Math.Max(1, text.Count(character => character == '\n') + 1);
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var error = new TextHighlighter { Background = transparent, Foreground = Brush("SystemFillColorCriticalBrush") };
        var warning = new TextHighlighter { Background = transparent, Foreground = Brush("SystemFillColorCautionBrush") };
        var debug = new TextHighlighter { Background = transparent, Foreground = Brush("TextFillColorTertiaryBrush") };
        foreach (var span in spans)
        {
            var range = new TextRange { StartIndex = span.Start, Length = span.Length };
            (span.Level switch
            {
                LogLineLevel.Error => error,
                LogLineLevel.Warning => warning,
                _ => debug
            }).Ranges.Add(range);
        }
        foreach (var highlighter in new[] { error, warning, debug })
        {
            if (highlighter.Ranges.Count > 0) LogContentText.TextHighlighters.Add(highlighter);
        }
        // One jump target per error entry: the first line of a stack trace block.
        var targets = new List<LogLineSpan>();
        var lastErrorLine = int.MinValue;
        foreach (var span in spans)
        {
            if (span.Level != LogLineLevel.Error) continue;
            if (lastErrorLine != span.Line - 1) targets.Add(span);
            lastErrorLine = span.Line;
        }
        errorSpans = targets;
        currentError = -1;
        UpdateErrorNavigation();
    }

    private void MarkCurrentError(LogLineSpan target)
    {
        if (currentErrorHighlighter is not null) LogContentText.TextHighlighters.Remove(currentErrorHighlighter);
        currentErrorHighlighter = new TextHighlighter
        {
            Background = Brush("SystemFillColorCriticalBackgroundBrush"),
            Foreground = Brush("SystemFillColorCriticalBrush")
        };
        currentErrorHighlighter.Ranges.Add(new TextRange { StartIndex = target.Start, Length = target.Length });
        LogContentText.TextHighlighters.Add(currentErrorHighlighter);
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private void UpdateErrorNavigation()
    {
        var any = errorSpans.Count > 0;
        PreviousErrorButton.IsEnabled = any;
        NextErrorButton.IsEnabled = any;
        ErrorPositionText.Text = !any
            ? string.Empty
            : currentError < 0
                ? AppLocalization.Format("LogsErrorCount", errorSpans.Count)
                : AppLocalization.Format("LogsErrorPosition", currentError + 1, errorSpans.Count);
    }

    private void PreviousError_Click(object sender, RoutedEventArgs e) => JumpToError(-1);

    private void NextError_Click(object sender, RoutedEventArgs e) => JumpToError(1);

    private void JumpToError(int direction)
    {
        if (errorSpans.Count == 0) return;
        currentError = currentError < 0
            ? (direction > 0 ? 0 : errorSpans.Count - 1)
            : (currentError + direction + errorSpans.Count) % errorSpans.Count;
        FollowTailToggle.IsChecked = false;
        var target = errorSpans[currentError];
        // NoWrap text: every line has the same height.
        var lineHeight = LogContentText.ActualHeight / displayedLineCount;
        var offset = Math.Max(0, target.Line * lineHeight - LogScroll.ViewportHeight / 3);
        programmaticScroll = true;
        LogScroll.ChangeView(0, offset, null, disableAnimation: !App.MainWindow.MotionEnabled);
        MarkCurrentError(target);
        UpdateErrorNavigation();
    }

    private void FollowTail_Click(object sender, RoutedEventArgs e)
    {
        if (FollowTail) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        LogScroll.UpdateLayout();
        programmaticScroll = true;
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, true);
    }

    // Scrolling up by hand pauses Follow; scrolling back to the end resumes it.
    private void LogScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate) return;
        if (programmaticScroll)
        {
            programmaticScroll = false;
            return;
        }
        var atBottom = LogScroll.ScrollableHeight <= 0
            || LogScroll.VerticalOffset >= LogScroll.ScrollableHeight - BottomTolerance;
        if (FollowTail != atBottom) FollowTailToggle.IsChecked = atBottom;
    }
}
