using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelloOtp.Core;

namespace HelloOtp;

/// <summary>A frozen, dimmed picture of one monitor that the user drags a selection on.</summary>
public partial class SnipOverlay : Window
{
    private const int MinimumSelectionPixels = 8;

    private readonly BitmapSource _image;
    private readonly PixelRect _bounds;
    private readonly bool _takeFocus;
    private readonly Action<PixelRect?> _done;
    private Point _start;
    private Point _end;
    private bool _dragging;
    private bool _finished;

    internal SnipOverlay(BitmapSource image, PixelRect bounds, bool takeFocus, Action<PixelRect?> done)
    {
        _image = image;
        _bounds = bounds;
        _takeFocus = takeFocus;
        _done = done;
        InitializeComponent();
        Screen.Source = image;

        SourceInitialized += (_, _) =>
        {
            Native.MakeToolWindow(this);
            Native.ExcludeFromCapture(this);
            Native.SetBounds(Native.Handle(this), _bounds);
        };
        Loaded += OnLoaded;
        Root.SizeChanged += (_, _) => UpdateDim();
        Closed += (_, _) => Finish(null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Moving to a monitor with a different scale can resize the window, so this applies the bounds again.
        var handle = Native.Handle(this);
        Native.SetBounds(handle, _bounds);
        if (_takeFocus)
        {
            Activate();
            Native.SetForegroundWindow(handle);
            Focus();
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Activate();
        _start = _end = e.GetPosition(Root);
        _dragging = true;
        CaptureMouse();
        UpdateSelection();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        _end = e.GetPosition(Root);
        UpdateSelection();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ReleaseMouseCapture();

        var area = Placement.SelectionToPixels(_start.X, _start.Y, _end.X, _end.Y, Root.ActualWidth, Root.ActualHeight, _image.PixelWidth, _image.PixelHeight);
        if (area.Width >= MinimumSelectionPixels && area.Height >= MinimumSelectionPixels)
        {
            Finish(area);
            return;
        }

        // A plain click selects nothing, so the user can just drag again.
        _end = _start;
        UpdateSelection();
    }

    private void OnCancelClick(object sender, MouseButtonEventArgs e) => Finish(null);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Finish(null);
    }

    private void Finish(PixelRect? area)
    {
        if (_finished)
            return;
        _finished = true;
        _done(area);
    }

    internal void ShowSelection(Point start, Point end)
    {
        _start = start;
        _end = end;
        UpdateSelection();
    }

    private Rect SelectionRect() => new(_start, _end);

    private void UpdateSelection()
    {
        var rect = SelectionRect();
        Selection.Visibility = rect.Width > 0 && rect.Height > 0 ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Controls.Canvas.SetLeft(Selection, rect.Left);
        System.Windows.Controls.Canvas.SetTop(Selection, rect.Top);
        Selection.Width = rect.Width;
        Selection.Height = rect.Height;
        UpdateDim();
    }

    private void UpdateDim()
    {
        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight)));
        var selection = SelectionRect();
        if (selection.Width > 0 && selection.Height > 0)
            geometry.Children.Add(new RectangleGeometry(selection));
        Dim.Data = geometry;
    }
}
