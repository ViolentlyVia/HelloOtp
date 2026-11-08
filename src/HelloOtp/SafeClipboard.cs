using System.Text;
using System.Windows.Threading;

namespace HelloOtp;

/// <summary>
/// Copies text with the formats that keep it out of Windows clipboard history and cloud sync, then
/// clears it after a delay if nothing else has been copied since.
/// </summary>
internal sealed class SafeClipboard
{
    private static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _timer;
    private uint _sequence;

    public SafeClipboard()
    {
        _timer = new DispatcherTimer { Interval = ClearAfter };
        _timer.Tick += (_, _) => ClearIfStillOurs();
    }

    public bool SetText(string text)
    {
        if (!Open())
            return false;
        try
        {
            Native.EmptyClipboard();
            const uint CF_UNICODETEXT = 13;
            var dwordZero = new byte[4];
            var ok = Native.SetClipboardBytes(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
            ok &= Native.SetClipboardBytes(Native.RegisterClipboardFormat("CanIncludeInClipboardHistory"), dwordZero);
            ok &= Native.SetClipboardBytes(Native.RegisterClipboardFormat("CanUploadToCloudClipboard"), dwordZero);
            ok &= Native.SetClipboardBytes(Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"), dwordZero);
            if (!ok)
            {
                // Without the exclusion formats the text must not stay on the clipboard.
                Native.EmptyClipboard();
                return false;
            }
        }
        finally
        {
            Native.CloseClipboard();
        }

        _sequence = Native.GetClipboardSequenceNumber();
        _timer.Stop();
        _timer.Start();
        return true;
    }

    public void ClearIfStillOurs()
    {
        if (!_timer.IsEnabled)
            return;
        _timer.Stop();
        if (Native.GetClipboardSequenceNumber() != _sequence || !Open())
            return;
        Native.EmptyClipboard();
        Native.CloseClipboard();
    }

    private static bool Open()
    {
        // Another program can hold the clipboard briefly.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (Native.OpenClipboard(IntPtr.Zero))
                return true;
            Thread.Sleep(20);
        }
        return false;
    }
}
