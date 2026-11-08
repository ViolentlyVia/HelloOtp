using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HelloOtp.Core;

namespace HelloOtp;

/// <summary>The Win32 calls WPF does not expose.</summary>
internal static class Native
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MONITORINFOF_PRIMARY = 1;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint ASFW_ANY = 0xFFFFFFFF;

    public const int WM_DISPLAYCHANGE = 0x007E;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string? title);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static IntPtr Handle(Window window) => new WindowInteropHelper(window).EnsureHandle();

    /// <summary>Keeps a window out of Alt-Tab and stops clicks on it from taking focus.</summary>
    public static void MakeToolWindowNoActivate(Window window)
    {
        var handle = Handle(window);
        var style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    public static void MakeToolWindow(Window window)
    {
        var handle = Handle(window);
        var style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW));
    }

    public static void ExcludeFromCapture(Window window) => SetWindowDisplayAffinity(Handle(window), WDA_EXCLUDEFROMCAPTURE);

    public static void SetDarkTitleBar(Window window, bool dark)
    {
        var value = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle(window), DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    public static void Move(IntPtr window, int x, int y) =>
        SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    public static PixelRect GetRect(IntPtr window)
    {
        GetWindowRect(window, out var rect);
        return new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    public static MonitorArea MonitorOf(IntPtr window) =>
        ToArea(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST));

    public static List<MonitorArea> Monitors()
    {
        var monitors = new List<MonitorArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            monitors.Add(ToArea(monitor));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    private static MonitorArea ToArea(IntPtr monitor)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
        GetMonitorInfo(monitor, ref info);
        return new MonitorArea(
            info.szDevice,
            new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom),
            (info.dwFlags & MONITORINFOF_PRIMARY) != 0);
    }

    /// <summary>Lets the Windows Hello broker process take the foreground.</summary>
    public static void AllowAnyForeground() => AllowSetForegroundWindow(ASFW_ANY);

    public static IntPtr FindWindowByClass(string className) => FindWindow(className, null);

    // Finding the window to return to

    private const long WS_EX_TOPMOST = 0x00000008;
    private const int DWMWA_CLOAKED = 14;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "XamlExplorerHostIslandWindow",
        "Progman",
        "WorkerW",
    };

    private delegate bool WindowEnumProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowEnumProc callback, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, char[] buffer, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    public static MonitorArea MonitorAt(int x, int y) =>
        ToArea(MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST));

    /// <summary>
    /// The app window to return to after using the tray. Clicking the tray makes the taskbar the
    /// foreground window, so this finds the frontmost real app window behind it.
    /// </summary>
    public static IntPtr PreviousAppWindow()
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && !IsShellWindow(foreground))
            return foreground;

        var found = IntPtr.Zero;
        var ownProcess = (uint)Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            if (!IsReturnTarget(window, ownProcess))
                return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsShellWindow(IntPtr window)
    {
        var buffer = new char[128];
        var length = GetClassName(window, buffer, buffer.Length);
        return ShellClasses.Contains(new string(buffer, 0, length));
    }

    private static bool IsReturnTarget(IntPtr window, uint ownProcess)
    {
        if (!IsWindowVisible(window) || IsIconic(window) || GetWindowTextLength(window) == 0 || IsShellWindow(window))
            return false;
        GetWindowThreadProcessId(window, out var process);
        if (process == ownProcess)
            return false;
        var style = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
        if ((style & (WS_EX_TOOLWINDOW | WS_EX_TOPMOST)) != 0)
            return false;

        // Suspended store apps and windows on other virtual desktops report as visible but are cloaked.
        return DwmGetWindowAttribute(window, DWMWA_CLOAKED, out var cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    // In-memory encryption

    private const uint CRYPTPROTECTMEMORY_SAME_PROCESS = 0;
    public const int MemoryProtectionBlockSize = 16;

    [DllImport("crypt32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectMemory(byte[] data, uint size, uint flags);
    [DllImport("crypt32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectMemory(byte[] data, uint size, uint flags);

    /// <summary>Encrypts a buffer in place so that only this process can decrypt it.</summary>
    public static bool ProtectMemory(byte[] data) => CryptProtectMemory(data, (uint)data.Length, CRYPTPROTECTMEMORY_SAME_PROCESS);

    public static bool UnprotectMemory(byte[] data) => CryptUnprotectMemory(data, (uint)data.Length, CRYPTPROTECTMEMORY_SAME_PROCESS);

    // Clipboard

    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);

    /// <summary>Puts bytes on the already-open clipboard. The system owns the memory after success.</summary>
    public static bool SetClipboardBytes(uint format, byte[] data)
    {
        var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (memory == IntPtr.Zero)
            return false;
        var target = GlobalLock(memory);
        if (target == IntPtr.Zero)
        {
            GlobalFree(memory);
            return false;
        }
        Marshal.Copy(data, 0, target, data.Length);
        GlobalUnlock(memory);
        if (SetClipboardData(format, memory) != IntPtr.Zero)
            return true;
        GlobalFree(memory);
        return false;
    }
}
