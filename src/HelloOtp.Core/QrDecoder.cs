using ZXing;
using ZXing.Common;

namespace HelloOtp.Core;

/// <summary>Reads QR codes from raw pixels on this machine. Handles several codes in one screenshot.</summary>
public static class QrDecoder
{
    public static IReadOnlyList<string> DecodeBgra32(byte[] pixels, int width, int height)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new[] { BarcodeFormat.QR_CODE },
            },
        };
        var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var results = reader.DecodeMultiple(source);
        if (results == null || results.Length == 0)
            return Array.Empty<string>();
        return results.Select(result => result.Text).Distinct().ToList();
    }
}
