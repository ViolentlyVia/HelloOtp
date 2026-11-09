using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelloOtp.Core;
using Xunit;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using FormatException = System.FormatException;

namespace HelloOtp.Tests;

public class SnipTests
{
    private const string Link = "otpauth://totp/Example:alice?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Example";

    /// <summary>A QR code with no white border, drawn at the given number of pixels per module.</summary>
    private static (byte[] Pixels, int Width, int Height) TightQr(int pixelsPerModule, byte dark = 0, byte alpha = 255)
    {
        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 };
        var matrix = new QRCodeWriter().encode(Link, BarcodeFormat.QR_CODE, 1, 1, hints);
        var side = matrix.Width * pixelsPerModule;
        var pixels = new byte[side * side * 4];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var shade = matrix[x / pixelsPerModule, y / pixelsPerModule] ? dark : (byte)255;
                var i = (y * side + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = shade;
                pixels[i + 3] = alpha;
            }
        }
        return (pixels, side, side);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    public void ReadsTightSelections(int pixelsPerModule)
    {
        var (pixels, width, height) = TightQr(pixelsPerModule);
        Assert.Equal(Link, QrDecoder.DecodeSnip(pixels, width, height));
    }

    [Fact]
    public void ReadsCapturesWithEmptyAlpha()
    {
        var (pixels, width, height) = TightQr(4, alpha: 0);
        Assert.Equal(Link, QrDecoder.DecodeSnip(pixels, width, height));
    }

    [Fact]
    public void ReturnsNullWhenThereIsNoCode()
    {
        var pixels = new byte[200 * 200 * 4];
        Array.Fill(pixels, (byte)200);
        Assert.Null(QrDecoder.DecodeSnip(pixels, 200, 200));
    }

    [Fact]
    public void CropCopiesTheSelectedArea()
    {
        var pixels = new byte[4 * 3 * 4];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)i;

        var cropped = QrDecoder.CropBgra32(pixels, 4, new PixelRect(1, 1, 3, 3));
        Assert.Equal(2 * 2 * 4, cropped.Length);
        Assert.Equal(pixels[(1 * 4 + 1) * 4], cropped[0]);
        Assert.Equal(pixels[(2 * 4 + 2) * 4 + 3], cropped[^1]);
    }

    [Fact]
    public void CropThenDecodeFindsCodeInsideALargerScreen()
    {
        var (qr, side, _) = TightQr(4);
        const int screenWidth = 800, screenHeight = 600, left = 300, top = 120;
        var screen = new byte[screenWidth * screenHeight * 4];
        Array.Fill(screen, (byte)90);
        for (var row = 0; row < side; row++)
            Buffer.BlockCopy(qr, row * side * 4, screen, ((top + row) * screenWidth + left) * 4, side * 4);

        var area = new PixelRect(left, top, left + side, top + side);
        Assert.Equal(Link, QrDecoder.DecodeSnip(QrDecoder.CropBgra32(screen, screenWidth, area), area.Width, area.Height));
    }

    [Fact]
    public void SelectionConvertsFromScaledView() =>
        Assert.Equal(new PixelRect(150, 75, 300, 225), Placement.SelectionToPixels(100, 50, 200, 150, 1280, 720, 1920, 1080));

    [Fact]
    public void SelectionWorksDraggedBackwards() =>
        Assert.Equal(new PixelRect(10, 20, 40, 60), Placement.SelectionToPixels(40, 60, 10, 20, 100, 100, 100, 100));

    [Fact]
    public void SelectionStaysInsideTheImage() =>
        Assert.Equal(new PixelRect(0, 0, 100, 100), Placement.SelectionToPixels(-50, -50, 500, 500, 100, 100, 100, 100));

    [Fact]
    public void LinksParseBothFormats()
    {
        var single = OtpLinks.Parse(Link);
        Assert.Equal("Example", Assert.Single(single.Accounts).Issuer);
        Assert.Equal(1, single.BatchSize);
        Assert.Throws<FormatException>(() => OtpLinks.Parse("https://example.com/"));
    }

    [Fact]
    public void OverlayLoadsAndDimsAroundSelection() => WindowTests.OnSta(() =>
    {
        const int width = 640, height = 360;
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)170);
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);

        PixelRect? result = new PixelRect(1, 1, 2, 2);
        var overlay = new SnipOverlay(image, new PixelRect(0, 0, width, height), takeFocus: false, area => result = area);
        var root = overlay.Root;
        overlay.Content = null;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        overlay.ShowSelection(new Point(200, 100), new Point(400, 260));
        root.UpdateLayout();

        Assert.Equal(200, overlay.Selection.Width);
        Assert.Equal(160, overlay.Selection.Height);
        Assert.Equal(2, ((GeometryGroup)overlay.Dim.Data).Children.Count);

        var output = Environment.GetEnvironmentVariable("HELLOOTP_TEST_RENDER");
        if (output != null)
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(output + ".snip.png");
            encoder.Save(stream);
        }

        overlay.Close();
        Assert.Null(result);
    });
}
