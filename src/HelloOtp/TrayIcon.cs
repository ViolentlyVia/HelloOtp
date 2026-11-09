using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Windows.Threading;

namespace HelloOtp;

/// <summary>
/// The notification-area icon. Single click opens the codes, double click shows or hides the
/// floating lock icon, and right click shows the menu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly ToolStripMenuItem _floatingItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly DispatcherTimer _singleClick;
    private (int X, int Y) _clickPosition;

    internal Icon Image { get; }
    internal ContextMenuStrip Menu { get; }

    public TrayIcon(AppController controller)
    {
        Image = CreateImage();

        var colors = new MenuColors();
        Menu = new ContextMenuStrip
        {
            Renderer = new MenuRenderer(colors),
            BackColor = colors.ToolStripDropDownBackground,
            ForeColor = colors.Text,
            Font = new Font("Segoe UI", 9.75f),
            ShowCheckMargin = true,
            ShowImageMargin = false,
        };

        ToolStripMenuItem Add(string text, Action action)
        {
            var item = new ToolStripMenuItem(text) { ForeColor = colors.Text };
            item.Click += (_, _) => action();
            Menu.Items.Add(item);
            return item;
        }

        var open = Add("Open codes", () =>
        {
            var cursor = Native.CursorPosition();
            controller.TrayClicked(cursor.X, cursor.Y);
        });
        open.Font = new Font(Menu.Font, FontStyle.Bold);
        _floatingItem = Add("Show floating icon", () => controller.SetFloatingIconVisible(!controller.FloatingIconVisible));
        Menu.Items.Add(new ToolStripSeparator());
        Add("Import from Google Authenticator...", () => controller.OpenImport(manualFocus: false));
        Add("Add account...", () => controller.OpenImport(manualFocus: true));
        Add("Manage accounts...", controller.OpenManage);
        Add("Sync between PCs...", controller.OpenSync);
        Menu.Items.Add(new ToolStripSeparator());
        _startupItem = Add("Start with Windows", () => StartWithWindows.IsEnabled = !StartWithWindows.IsEnabled);
        Add("Quit", () => System.Windows.Application.Current.Shutdown());

        Menu.Opening += (_, _) =>
        {
            _floatingItem.Checked = controller.FloatingIconVisible;
            _startupItem.Checked = StartWithWindows.IsEnabled;
        };

        // A single click waits out the double-click time, so a double click never opens the codes.
        _singleClick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime) };
        _singleClick.Tick += (_, _) =>
        {
            _singleClick.Stop();
            controller.TrayClicked(_clickPosition.X, _clickPosition.Y);
        };

        _notify = new NotifyIcon { Icon = Image, Text = "HelloOtp", ContextMenuStrip = Menu };
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            _clickPosition = Native.CursorPosition();
            _singleClick.Stop();
            _singleClick.Start();
        };
        _notify.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            _singleClick.Stop();
            controller.SetFloatingIconVisible(!controller.FloatingIconVisible);
        };
    }

    public void Show() => _notify.Visible = true;

    public void Dispose()
    {
        _singleClick.Stop();

        // Hiding first removes the icon at once instead of leaving a ghost until the tray is hovered.
        _notify.Visible = false;
        _notify.Dispose();
        Menu.Dispose();
        Image.Dispose();
    }

    private static Icon CreateImage()
    {
        const int size = 64;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var accent = new SolidBrush(Color.FromArgb(76, 141, 255));
            graphics.FillEllipse(accent, 1, 1, size - 2, size - 2);

            using var shackle = new Pen(Color.White, 6);
            graphics.DrawArc(shackle, 23, 14, 18, 20, 180, 180);
            graphics.DrawLine(shackle, 23, 24, 23, 32);
            graphics.DrawLine(shackle, 41, 24, 41, 32);

            using var body = new GraphicsPath();
            var rect = new Rectangle(17, 30, 30, 22);
            const int diameter = 6;
            body.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            body.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            body.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            body.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            body.CloseFigure();
            graphics.FillPath(Brushes.White, body);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        private readonly Color _background = Theme.IsDark ? Color.FromArgb(32, 34, 37) : Color.FromArgb(250, 250, 251);
        private readonly Color _border = Theme.IsDark ? Color.FromArgb(60, 63, 68) : Color.FromArgb(213, 216, 221);
        private readonly Color _hover = Theme.IsDark ? Color.FromArgb(47, 50, 55) : Color.FromArgb(236, 238, 241);

        public Color Text { get; } = Theme.IsDark ? Color.FromArgb(242, 243, 245) : Color.FromArgb(28, 30, 33);
        public Color Accent { get; } = Theme.IsDark ? Color.FromArgb(76, 141, 255) : Color.FromArgb(37, 99, 235);

        public override Color ToolStripDropDownBackground => _background;
        public override Color ImageMarginGradientBegin => _background;
        public override Color ImageMarginGradientMiddle => _background;
        public override Color ImageMarginGradientEnd => _background;
        public override Color MenuBorder => _border;
        public override Color MenuItemBorder => _hover;
        public override Color MenuItemSelected => _hover;
        public override Color SeparatorDark => _border;
        public override Color SeparatorLight => _border;
        public override Color CheckBackground => _background;
        public override Color CheckSelectedBackground => _hover;
        public override Color CheckPressedBackground => _hover;
    }

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly MenuColors _colors;

        public MenuRenderer(MenuColors colors) : base(colors)
        {
            _colors = colors;
            RoundedEdges = false;
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // The built-in check mark is black, which disappears on a dark menu.
            var box = e.ImageRectangle;
            var x = box.Left + box.Width / 2f;
            var y = box.Top + box.Height / 2f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(_colors.Accent, 2f);
            e.Graphics.DrawLines(pen, new[]
            {
                new PointF(x - 5, y),
                new PointF(x - 1.5f, y + 3.5f),
                new PointF(x + 5, y - 4),
            });
        }
    }
}
