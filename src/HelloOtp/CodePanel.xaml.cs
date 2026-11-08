using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using HelloOtp.Core;

namespace HelloOtp;

/// <summary>The unlocked list of codes. Holds decrypted secrets only while it is open.</summary>
public partial class CodePanel : Window
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(60);

    private readonly UnlockedVault _vault;
    private readonly IntPtr _previousWindow;
    private readonly Func<string, bool> _copy;
    private readonly List<CodeRow> _rows;
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _timer;
    private PixelRect _anchor;
    private PixelRect _workArea;
    private DateTime _lastActivity = DateTime.UtcNow;
    private bool _closing;

    internal CodePanel(UnlockedVault vault, IntPtr previousWindow, Func<string, bool> copy)
    {
        _vault = vault;
        _previousWindow = previousWindow;
        _copy = copy;
        InitializeComponent();

        _rows = vault.Accounts
            .OrderBy(account => account.Issuer, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(account => account.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(account => new CodeRow(account))
            .ToList();
        UpdateRows();

        _view = CollectionViewSource.GetDefaultView(_rows);
        Rows.ItemsSource = _view;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Rows.SelectedIndex = _rows.Count > 0 ? 0 : -1;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += OnTick;

        SourceInitialized += (_, _) =>
        {
            Native.MakeToolWindow(this);
            Native.ExcludeFromCapture(this);
        };
        ContentRendered += OnContentRendered;
        Deactivated += (_, _) => RequestClose();
        Closing += (_, _) => _closing = true;
        Closed += OnClosed;
    }

    /// <summary>Shows the panel next to the anchor, inside the work area of the anchor's monitor.</summary>
    internal void ShowBeside(PixelRect anchor, PixelRect workArea)
    {
        _anchor = anchor;
        _workArea = workArea;

        // Start on the anchor's monitor so WPF lays the panel out at that monitor's scale.
        var start = Placement.Clamp(anchor.Left, anchor.Top, 1, 1, workArea);
        Native.Move(Native.Handle(this), start.X, start.Y);
        Show();
        _timer.Start();
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        // The height is now known. Fix it so filtering does not make the panel jump.
        var height = ActualHeight;
        SizeToContent = SizeToContent.Manual;
        Height = height;

        var handle = Native.Handle(this);
        var panel = Native.GetRect(handle);
        var position = Placement.BesideIcon(_anchor, panel.Width, panel.Height, _workArea);
        Native.Move(handle, position.X, position.Y);
        Opacity = 1;

        Activate();
        Native.SetForegroundWindow(handle);
        SearchBox.Focus();
    }

    private void UpdateRows()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var row in _rows)
            row.Update(now);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow - _lastActivity >= IdleLimit)
        {
            RequestClose();
            return;
        }
        UpdateRows();
    }

    private void OnActivity(object sender, MouseEventArgs e) => _lastActivity = DateTime.UtcNow;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _lastActivity = DateTime.UtcNow;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                RequestClose();
                ReturnFocus();
                break;
            case Key.Enter:
                e.Handled = true;
                if (Rows.SelectedItem is CodeRow selected)
                    CopyAndClose(selected);
                break;
            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (Rows.Items.Count == 0)
            return;
        Rows.SelectedIndex = Math.Clamp(Rows.SelectedIndex + delta, 0, Rows.Items.Count - 1);
        Rows.ScrollIntoView(Rows.SelectedItem);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var filter = SearchBox.Text.Trim();
        _view.Filter = filter.Length == 0 ? null : item => ((CodeRow)item).Matches(filter);
        Rows.SelectedIndex = Rows.Items.Count > 0 ? 0 : -1;
    }

    private void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: CodeRow row })
        {
            e.Handled = true;
            CopyAndClose(row);
        }
    }

    private void CopyAndClose(CodeRow row)
    {
        // Recompute in case the code rolled over since the last timer tick.
        row.Update(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (!_copy(row.Code))
        {
            MessageBox.Show("The clipboard is busy or could not be protected, so the code was not copied.",
                "HelloOtp", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        RequestClose();
        ReturnFocus();
    }

    private void ReturnFocus()
    {
        if (_previousWindow != IntPtr.Zero)
            Native.SetForegroundWindow(_previousWindow);
    }

    private void RequestClose()
    {
        if (_closing)
            return;
        _closing = true;

        // Closing from inside an activation change is not allowed, so it waits for the next dispatcher turn.
        Hide();
        _timer.Stop();
        Dispatcher.BeginInvoke(Close);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        Rows.ItemsSource = null;
        _rows.Clear();
        _vault.Dispose();
    }
}
