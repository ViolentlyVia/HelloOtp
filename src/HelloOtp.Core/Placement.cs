namespace HelloOtp.Core;

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public readonly record struct MonitorArea(string Device, PixelRect WorkArea, bool IsPrimary);

/// <summary>Window position arithmetic, in physical pixels.</summary>
public static class Placement
{
    private const int DefaultMargin = 24;

    public static (int X, int Y) Clamp(int x, int y, int width, int height, PixelRect work)
    {
        var clampedX = Math.Max(work.Left, Math.Min(x, work.Right - width));
        var clampedY = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
        return (clampedX, clampedY);
    }

    /// <summary>Beside the icon on the right, or on the left when the right side has no room.</summary>
    public static (int X, int Y) BesideIcon(PixelRect icon, int width, int height, PixelRect work, int gap = 8)
    {
        var x = icon.Right + gap;
        if (x + width > work.Right)
            x = icon.Left - gap - width;
        return Clamp(x, icon.Top, width, height, work);
    }

    /// <summary>Converts a selection dragged in a scaled view into pixels of the image it shows, kept inside the image.</summary>
    public static PixelRect SelectionToPixels(double x1, double y1, double x2, double y2, double viewWidth, double viewHeight, int pixelWidth, int pixelHeight)
    {
        var scaleX = pixelWidth / viewWidth;
        var scaleY = pixelHeight / viewHeight;
        var left = Math.Clamp((int)Math.Floor(Math.Min(x1, x2) * scaleX), 0, pixelWidth);
        var top = Math.Clamp((int)Math.Floor(Math.Min(y1, y2) * scaleY), 0, pixelHeight);
        var right = Math.Clamp((int)Math.Ceiling(Math.Max(x1, x2) * scaleX), 0, pixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(Math.Max(y1, y2) * scaleY), 0, pixelHeight);
        return new PixelRect(left, top, right, bottom);
    }

    /// <summary>The icon's saved spot, or the primary monitor's top-right corner if that monitor is gone.</summary>
    public static (int X, int Y) RestoreIcon(string? device, int offsetX, int offsetY, int size, IReadOnlyList<MonitorArea> monitors)
    {
        foreach (var monitor in monitors)
        {
            if (monitor.Device == device)
                return Clamp(monitor.WorkArea.Left + offsetX, monitor.WorkArea.Top + offsetY, size, size, monitor.WorkArea);
        }

        var primary = monitors.FirstOrDefault(monitor => monitor.IsPrimary);
        var work = primary.Device != null ? primary.WorkArea : monitors[0].WorkArea;
        return Clamp(work.Right - size - DefaultMargin, work.Top + DefaultMargin, size, size, work);
    }
}
