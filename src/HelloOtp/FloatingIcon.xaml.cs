using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using HelloOtp.Core;

namespace HelloOtp;

public partial class FloatingIcon : Window
{
    private const int DragThreshold = 4;

    private readonly AppController _controller;
    private IntPtr _handle;
    private bool _pressed;
    private bool _dragging;
    private (int X, int Y) _pressCursor;
    private PixelRect _pressRect;

    internal FloatingIcon(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = Native.Handle(this);
        Native.MakeToolWindowNoActivate(this);
        HwndSource.FromHwnd(_handle)!.AddHook(WindowProc);
        RestorePosition();
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.WM_DISPLAYCHANGE)
            Dispatcher.BeginInvoke(RestorePosition);
        return IntPtr.Zero;
    }

    private void RestorePosition()
    {
        var settings = _controller.Settings;
        var position = Placement.RestoreIcon(
            settings.IconDevice, settings.IconOffsetX, settings.IconOffsetY,
            Native.GetRect(_handle).Width, Native.Monitors());
        Native.Move(_handle, position.X, position.Y);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        _dragging = false;
        _pressCursor = Native.CursorPosition();
        _pressRect = Native.GetRect(_handle);
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed)
            return;

        var cursor = Native.CursorPosition();
        var dx = cursor.X - _pressCursor.X;
        var dy = cursor.Y - _pressCursor.Y;
        if (!_dragging && Math.Max(Math.Abs(dx), Math.Abs(dy)) < DragThreshold)
            return;

        _dragging = true;
        Native.Move(_handle, _pressRect.Left + dx, _pressRect.Top + dy);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
            return;
        _pressed = false;
        ReleaseMouseCapture();

        if (_dragging)
            FinishDrag();
        else
            _controller.IconClicked(Native.GetForegroundWindow());
    }

    private void FinishDrag()
    {
        var monitor = Native.MonitorOf(_handle);
        var rect = Native.GetRect(_handle);
        var position = Placement.Clamp(rect.Left, rect.Top, rect.Width, rect.Height, monitor.WorkArea);
        Native.Move(_handle, position.X, position.Y);

        var settings = _controller.Settings;
        settings.IconDevice = monitor.Device;
        settings.IconOffsetX = position.X - monitor.WorkArea.Left;
        settings.IconOffsetY = position.Y - monitor.WorkArea.Top;
        _controller.SaveSettings();
    }

    private void OnMenuOpened(object sender, RoutedEventArgs e) => StartupItem.IsChecked = StartWithWindows.IsEnabled;

    private void OnMinimize(object sender, RoutedEventArgs e) => _controller.SetFloatingIconVisible(false);

    private void OnImport(object sender, RoutedEventArgs e) => _controller.OpenImport(manualFocus: false);

    private void OnAdd(object sender, RoutedEventArgs e) => _controller.OpenImport(manualFocus: true);

    private void OnManage(object sender, RoutedEventArgs e) => _controller.OpenManage();

    private void OnSync(object sender, RoutedEventArgs e) => _controller.OpenSync();

    private void OnToggleStartup(object sender, RoutedEventArgs e) => StartWithWindows.IsEnabled = StartupItem.IsChecked;

    private void OnQuit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
