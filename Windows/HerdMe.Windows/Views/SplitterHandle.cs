using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HerdMe.Windows.Views;

/// <summary>
/// A thin vertical splitter between two panes. Drag with the mouse, pen or touch, or focus it
/// and use the arrow keys (Home resets). Deltas are reported in the parent's coordinate space,
/// so a positive delta always means "the leading pane grows", in left-to-right and
/// right-to-left layouts alike.
/// </summary>
public sealed partial class SplitterHandle : ContentControl
{
    private const double KeyboardStep = 16;
    private readonly Rectangle line;
    private bool dragging;
    private double dragOrigin;

    public SplitterHandle()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Background = new SolidColorBrush(Colors.Transparent);
        line = new Rectangle
        {
            Width = 2,
            RadiusX = 1,
            RadiusY = 1,
            Margin = new Thickness(0, 24, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0,
            IsHitTestVisible = false
        };
        line.SetValue(Shape.FillProperty, Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"]);
        Content = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Children = { line }
        };
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }

    public event EventHandler? DragStarted;

    // Cumulative delta since DragStarted.
    public event EventHandler<double>? DragDelta;

    public event EventHandler? DragCompleted;

    // Home: the page restores the default width.
    public event EventHandler? ResetRequested;

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        line.Opacity = 1;
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        if (!dragging) line.Opacity = 0;
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Parent is not UIElement parent || !CapturePointer(e.Pointer)) return;
        dragging = true;
        dragOrigin = e.GetCurrentPoint(parent).Position.X;
        line.Opacity = 1;
        e.Handled = true;
        DragStarted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!dragging || Parent is not UIElement parent) return;
        e.Handled = true;
        DragDelta?.Invoke(this, e.GetCurrentPoint(parent).Position.X - dragOrigin);
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag();
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        var rightToLeft = FlowDirection == FlowDirection.RightToLeft;
        double step;
        switch (e.Key)
        {
            case global::Windows.System.VirtualKey.Left:
                step = rightToLeft ? KeyboardStep : -KeyboardStep;
                break;
            case global::Windows.System.VirtualKey.Right:
                step = rightToLeft ? -KeyboardStep : KeyboardStep;
                break;
            case global::Windows.System.VirtualKey.Home:
                e.Handled = true;
                ResetRequested?.Invoke(this, EventArgs.Empty);
                return;
            default:
                return;
        }
        e.Handled = true;
        DragStarted?.Invoke(this, EventArgs.Empty);
        DragDelta?.Invoke(this, step);
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnGotFocus(RoutedEventArgs e)
    {
        base.OnGotFocus(e);
        line.Opacity = 1;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (!dragging) line.Opacity = 0;
    }

    private void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        line.Opacity = FocusState == FocusState.Unfocused ? 0 : 1;
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }
}
