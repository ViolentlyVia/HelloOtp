using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelloOtp.Core;

namespace HelloOtp;

internal sealed record Snip(byte[] Pixels, int Width, int Height);

/// <summary>
/// Freezes the screen and lets the user drag around part of it, like the Snipping Tool. Uses one
/// overlay per monitor so each one lines up exactly at that monitor's scale.
/// </summary>
internal static class ScreenSnip
{
    /// <summary>Completes with the selected pixels for the caller to wipe, or null if cancelled.</summary>
    public static Task<Snip?> SelectAsync()
    {
        var monitors = Native.MonitorBounds();
        var captures = monitors.Select(Capture).ToList();
        var overlays = new List<SnipOverlay>();
        var completion = new TaskCompletionSource<Snip?>();
        var finished = false;

        void Finish(Snip? result)
        {
            if (finished)
                return;
            finished = true;
            foreach (var overlay in overlays)
                overlay.Close();
            foreach (var capture in captures)
                SecretBuffer.Wipe(capture);
            completion.SetResult(result);
        }

        var cursor = Native.CursorPosition();
        for (var i = 0; i < monitors.Count; i++)
        {
            var bounds = monitors[i];
            var pixels = captures[i];
            var image = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgr32, null, pixels, bounds.Width * 4);
            image.Freeze();

            var underCursor = cursor.X >= bounds.Left && cursor.X < bounds.Right && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;
            overlays.Add(new SnipOverlay(image, bounds, underCursor, area =>
            {
                if (area is not { } selected)
                {
                    Finish(null);
                    return;
                }
                Finish(new Snip(QrDecoder.CropBgra32(pixels, bounds.Width, selected), selected.Width, selected.Height));
            }));
        }

        foreach (var overlay in overlays)
            overlay.Show();
        return completion.Task;
    }

    private static byte[] Capture(PixelRect bounds)
    {
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);

        var locked = bitmap.LockBits(new Rectangle(0, 0, bounds.Width, bounds.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[bounds.Width * bounds.Height * 4];
            for (var row = 0; row < bounds.Height; row++)
                Marshal.Copy(locked.Scan0 + row * locked.Stride, pixels, row * bounds.Width * 4, bounds.Width * 4);
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }
    }
}
