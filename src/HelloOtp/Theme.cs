using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace HelloOtp;

/// <summary>Follows the Windows light or dark app setting.</summary>
internal static class Theme
{
    public static bool IsDark { get; private set; }

    private static bool WindowsIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Apply()
    {
        IsDark = WindowsIsDark();
        Set("PanelBackground", IsDark ? "#FF202225" : "#FFFAFAFB");
        Set("PanelBorder", IsDark ? "#FF3C3F44" : "#FFD5D8DD");
        Set("TextPrimary", IsDark ? "#FFF2F3F5" : "#FF1C1E21");
        Set("TextSecondary", IsDark ? "#FFA3A8B0" : "#FF62676F");
        Set("InputBackground", IsDark ? "#FF2B2D31" : "#FFFFFFFF");
        Set("RowHover", IsDark ? "#FF2F3237" : "#FFECEEF1");
        Set("RowSelected", IsDark ? "#FF383C43" : "#FFE1E5EA");
        Set("ButtonBackground", IsDark ? "#FF34373C" : "#FFE8EAED");
        Set("Accent", IsDark ? "#FF4C8DFF" : "#FF2563EB");
        Set("Danger", IsDark ? "#FFFF6B6B" : "#FFD32F2F");
    }

    private static void Set(string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    /// <summary>Dark title bar when needed, and excluded from screenshots, screen sharing and Recall.</summary>
    public static void PrepareSensitiveWindow(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            Native.SetDarkTitleBar(window, IsDark);
            Native.ExcludeFromCapture(window);
        };
    }
}
