using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelloOtp.Core;
using Microsoft.Win32;

namespace HelloOtp;

public partial class ImportWindow : Window
{
    private readonly AppController _controller;
    private readonly List<OtpAccount> _staged = new();
    private readonly List<string> _warnings = new();
    private readonly Dictionary<int, (int Size, HashSet<int> Seen)> _batches = new();

    internal ImportWindow(AppController controller, bool manualFocus)
    {
        _controller = controller;
        InitializeComponent();
        Theme.PrepareSensitiveWindow(this);
        Loaded += (_, _) => (manualFocus ? IssuerBox : LinksBox).Focus();
        Closed += (_, _) => WipeStaged();
    }

    private void OnChooseImages(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose Google Authenticator QR images",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        ClearError();
        foreach (var path in dialog.FileNames)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                ProcessBitmap(frame, forceOpaque: false, Path.GetFileName(path));
            }
            catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
            {
                ShowError($"{Path.GetFileName(path)} could not be read as an image.");
            }
        }
        Refresh();
    }

    private void OnPasteImage(object sender, RoutedEventArgs e)
    {
        ClearError();
        var image = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
        if (image == null)
        {
            ShowError("The clipboard has no image.");
            return;
        }

        // Screenshots often arrive with an empty alpha channel.
        ProcessBitmap(image, forceOpaque: true, "The clipboard image");
        Refresh();
    }

    private void ProcessBitmap(BitmapSource source, bool forceOpaque, string name)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        if (forceOpaque)
        {
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }

        var links = QrDecoder.DecodeBgra32(pixels, converted.PixelWidth, converted.PixelHeight);
        if (links.Count == 0)
        {
            ShowError($"{name} has no readable QR code.");
            return;
        }
        foreach (var link in links)
            ProcessLink(link, name);
    }

    private void OnAddLinks(object sender, RoutedEventArgs e)
    {
        ClearError();
        foreach (var line in LinksBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            ProcessLink(line, "A pasted line");
        LinksBox.Clear();
        Refresh();
    }

    private void ProcessLink(string link, string source)
    {
        try
        {
            if (MigrationPayload.IsMatch(link))
            {
                var batch = MigrationPayload.Parse(link);
                AccountMerge.Merge(_staged, batch.Accounts);
                _warnings.AddRange(batch.Skipped);
                if (!_batches.TryGetValue(batch.BatchId, out var seen))
                    _batches[batch.BatchId] = seen = (batch.BatchSize, new HashSet<int>());
                seen.Seen.Add(batch.BatchIndex);
            }
            else if (OtpAuthUri.IsMatch(link))
            {
                AccountMerge.Merge(_staged, new[] { OtpAuthUri.Parse(link) });
            }
            else
            {
                ShowError($"{source} is not a two-factor link.");
            }
        }
        catch (FormatException error)
        {
            ShowError($"{source}: {error.Message}");
        }
    }

    private void OnAddManual(object sender, RoutedEventArgs e)
    {
        ClearError();
        var issuer = IssuerBox.Text.Trim();
        var label = LabelBox.Text.Trim();
        if (issuer.Length == 0 && label.Length == 0)
        {
            ShowError("Enter a site or account name.");
            return;
        }

        try
        {
            AccountMerge.Merge(_staged, new[] { new OtpAccount(issuer, label, Base32.Decode(SecretBox.Text)) });
        }
        catch (FormatException error)
        {
            ShowError(error.Message);
            return;
        }
        IssuerBox.Clear();
        LabelBox.Clear();
        SecretBox.Clear();
        Refresh();
    }

    private void Refresh()
    {
        SummaryText.Text = _staged.Count switch
        {
            0 => "Nothing ready to save yet.",
            1 => "1 account ready to save.",
            _ => $"{_staged.Count} accounts ready to save.",
        };
        SaveButton.IsEnabled = _staged.Count > 0;

        var detail = new StringBuilder();
        foreach (var account in _staged)
            detail.AppendLine(account.Issuer.Length > 0 ? $"{account.Issuer} ({account.Label})" : account.Label);
        foreach (var (size, seen) in _batches.Values)
        {
            var missing = Enumerable.Range(1, size).Where(number => !seen.Contains(number - 1)).ToList();
            if (missing.Count > 0)
                detail.AppendLine($"Google split this export into {size} QR codes. Still missing: {string.Join(", ", missing)}.");
        }
        foreach (var warning in _warnings)
            detail.AppendLine("Skipped " + warning);
        DetailText.Text = detail.ToString().TrimEnd();
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        ClearError();
        SaveButton.IsEnabled = false;
        var added = await _controller.AddAccountsAsync(_staged);
        if (added == null)
        {
            SaveButton.IsEnabled = _staged.Count > 0;
            return;
        }

        // The merge moved or wiped every staged account.
        var duplicates = _staged.Count - added.Value;
        _staged.Clear();
        MessageBox.Show(
            duplicates > 0 ? $"Added {added}. Skipped {duplicates} already in your vault." : $"Added {added}.",
            "HelloOtp", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void WipeStaged()
    {
        foreach (var account in _staged)
            account.Dispose();
        _staged.Clear();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = ErrorText.Visibility == Visibility.Visible ? ErrorText.Text + "\n" + message : message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorText.Text = "";
        ErrorText.Visibility = Visibility.Collapsed;
    }
}
