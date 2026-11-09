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

    /// <summary>
    /// Reads one QR code from a hand-drawn selection. A tight selection has no white border and a
    /// code on screen can be small, so this adds a border and retries at larger sizes.
    /// </summary>
    public static string? DecodeSnip(byte[] pixels, int width, int height)
    {
        const int maxSide = 3000;
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new[] { BarcodeFormat.QR_CODE },
            },
        };

        foreach (var scale in new[] { 1, 2, 3, 4 })
        {
            if (Math.Max(width, height) * scale > maxSide)
                break;

            var prepared = ScaleWithBorder(pixels, width, height, scale, out var preparedWidth, out var preparedHeight);
            try
            {
                var result = reader.Decode(new RGBLuminanceSource(prepared, preparedWidth, preparedHeight, RGBLuminanceSource.BitmapFormat.BGRA32));
                if (result != null)
                    return result.Text;
            }
            finally
            {
                SecretBuffer.Wipe(prepared);
            }
        }
        return null;
    }

    public static byte[] CropBgra32(byte[] pixels, int width, PixelRect area)
    {
        var cropped = new byte[area.Width * area.Height * 4];
        for (var row = 0; row < area.Height; row++)
            Buffer.BlockCopy(pixels, ((area.Top + row) * width + area.Left) * 4, cropped, row * area.Width * 4, area.Width * 4);
        return cropped;
    }

    private static byte[] ScaleWithBorder(byte[] pixels, int width, int height, int scale, out int outputWidth, out int outputHeight)
    {
        var border = Math.Max(16, Math.Max(width, height) * scale / 8);
        outputWidth = width * scale + border * 2;
        outputHeight = height * scale + border * 2;

        var output = new byte[outputWidth * outputHeight * 4];
        Array.Fill(output, (byte)255);
        for (var y = 0; y < height * scale; y++)
        {
            var source = (y / scale) * width * 4;
            var target = (y + border) * outputWidth * 4 + border * 4;
            for (var x = 0; x < width * scale; x++)
            {
                var from = source + (x / scale) * 4;
                var to = target + x * 4;
                output[to] = pixels[from];
                output[to + 1] = pixels[from + 1];
                output[to + 2] = pixels[from + 2];

                // Screen captures can have an empty alpha channel, which would read as blank.
                output[to + 3] = 255;
            }
        }
        return output;
    }
}

/// <summary>Turns the text of a QR code into accounts.</summary>
public static class OtpLinks
{
    public static MigrationBatch Parse(string text)
    {
        if (MigrationPayload.IsMatch(text))
            return MigrationPayload.Parse(text);
        if (OtpAuthUri.IsMatch(text))
        {
            var batch = new MigrationBatch();
            batch.Accounts.Add(OtpAuthUri.Parse(text));
            return batch;
        }
        throw new System.FormatException("That QR code is not a two-factor code.");
    }
}
