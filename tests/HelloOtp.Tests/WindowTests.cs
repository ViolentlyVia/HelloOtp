using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelloOtp.Core;
using Xunit;

namespace HelloOtp.Tests;

/// <summary>
/// Loads the real windows and lays out their content without ever showing one, so markup errors
/// fail here and no test takes focus or needs Windows Hello.
/// </summary>
public class WindowTests
{
    private static readonly object Gate = new();
    private static App? _app;

    internal static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                lock (Gate)
                {
                    if (Application.Current == null)
                    {
                        _app = new App();
                        _app.InitializeComponent();
                        Theme.Apply();
                    }
                    action();
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static UnlockedVault SampleVault()
    {
        var vault = Vault.Create(new byte[256], new byte[20]);
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        vault.Accounts.Add(new OtpAccount("Zeta", "z@example.com", SecretBuffer.CopyOf(secret)));
        vault.Accounts.Add(new OtpAccount("Alpha", "a@example.com", SecretBuffer.CopyOf(secret), OtpAlgorithm.Sha1, 8));
        vault.Accounts.Add(new OtpAccount("", "no-issuer", SecretBuffer.CopyOf(secret)));
        return vault;
    }

    private static FrameworkElement Detach(Window window, double width, double height)
    {
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        return root;
    }

    [Fact]
    public void CodePanelListsAccountsSorted() => OnSta(() =>
    {
        var vault = SampleVault();
        var panel = new CodePanel(vault, IntPtr.Zero, _ => true, () => { });
        var rows = panel.Rows.Items.Cast<CodeRow>().ToList();
        Assert.Equal(new[] { "no-issuer", "Alpha", "Zeta" }, rows.Select(row => row.Title));
        Assert.Equal(7, rows[0].Display.Length);
        Assert.Equal(9, rows[1].Display.Length);
        Assert.All(rows, row => Assert.InRange(row.Fraction, 0.0, 1.0));

        var root = Detach(panel, 340, 560);
        Assert.True(root.ActualHeight > 0);

        var output = Environment.GetEnvironmentVariable("HELLOOTP_TEST_RENDER");
        if (output != null)
        {
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(output);
            encoder.Save(stream);
        }
        vault.Dispose();
    });

    [Fact]
    public void CodePanelFilters() => OnSta(() =>
    {
        var vault = SampleVault();
        var panel = new CodePanel(vault, IntPtr.Zero, _ => true, () => { });
        panel.SearchBox.Text = "alp";
        Assert.Equal("Alpha", Assert.Single(panel.Rows.Items.Cast<CodeRow>()).Title);
        Assert.Equal(0, panel.Rows.SelectedIndex);
        panel.SearchBox.Text = "nothing matches";
        Assert.Empty(panel.Rows.Items.Cast<CodeRow>());
        vault.Dispose();
    });

    [Fact]
    public void RowComputesRfcCode()
    {
        using var account = new OtpAccount("A", "b", Encoding.ASCII.GetBytes("12345678901234567890"));
        var row = new CodeRow(account);
        row.Update(59_000);
        Assert.Equal("287082", row.Code);
        Assert.Equal("287 082", row.Display);
        Assert.True(row.Expiring);
        row.Update(60_000);
        Assert.False(row.Expiring);
        Assert.Equal(1.0, row.Fraction);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }

    [Fact]
    public void RightClickMenuUsesThemeColors() => OnSta(() =>
    {
        var menu = new FloatingIcon(new AppController()).ContextMenu;
        menu.Measure(new Size(400, 400));
        menu.Arrange(new Rect(menu.DesiredSize));
        menu.UpdateLayout();

        var quit = Descendants(menu).OfType<TextBlock>().Single(text => text.Text == "Quit");
        Assert.Same(Application.Current.Resources["TextPrimary"], quit.Foreground);
        var border = Assert.IsType<Border>(VisualTreeHelper.GetChild(menu, 0));
        Assert.Same(Application.Current.Resources["PanelBackground"], border.Background);

        var output = Environment.GetEnvironmentVariable("HELLOOTP_TEST_RENDER");
        if (output != null)
        {
            var bitmap = new RenderTargetBitmap((int)menu.ActualWidth, (int)menu.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(menu);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(output + ".menu.png");
            encoder.Save(stream);
        }
    });

    [Fact]
    public void TrayMenuAndImageBuild() => OnSta(() =>
    {
        using var tray = new TrayIcon(new AppController());
        var labels = tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Select(item => item.Text).ToList();
        Assert.Contains("Open codes", labels);
        Assert.Contains("Show floating icon", labels);
        Assert.Contains("Quit", labels);
        Assert.True(tray.Image.Width > 0);

        var output = Environment.GetEnvironmentVariable("HELLOOTP_TEST_RENDER");
        if (output != null)
        {
            ((System.Windows.Forms.ToolStripMenuItem)tray.Menu.Items[1]).Checked = true;
            tray.Menu.PerformLayout();
            using var bitmap = new System.Drawing.Bitmap(tray.Menu.PreferredSize.Width, tray.Menu.PreferredSize.Height);
            tray.Menu.Size = tray.Menu.PreferredSize;
            tray.Menu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(output + ".tray.png");
            using var icon = tray.Image.ToBitmap();
            icon.Save(output + ".trayicon.png");
        }
    });

    [Fact]
    public void FloatingIconCanMinimizeToTray() => OnSta(() =>
    {
        var menu = new FloatingIcon(new AppController()).ContextMenu;
        Assert.Contains(menu.Items.OfType<MenuItem>(), item => (string)item.Header == "Minimize to tray");
    });

    [Fact]
    public void OtherWindowsLoad() => OnSta(() =>
    {
        var controller = new AppController();
        Detach(new FloatingIcon(controller), 48, 48);
        Detach(new SetupWindow("ABCD-EFGH", _ => { }), 500, 600);
        Detach(new RecoveryWindow("Test.", _ => true), 500, 600);
        Detach(new ImportWindow(controller, manualFocus: false), 540, 800);
        var vault = SampleVault();
        var manage = new ManageWindow(vault, _ => { });
        Detach(manage, 560, 480);
        vault.Dispose();
    });
}
