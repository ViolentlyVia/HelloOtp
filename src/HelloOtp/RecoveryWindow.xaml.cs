using System.Windows;
using HelloOtp.Core;

namespace HelloOtp;

public partial class RecoveryWindow : Window
{
    private readonly Func<byte[], bool> _tryOpen;

    internal RecoveryWindow(string reason, Func<byte[], bool> tryOpen, string? heading = null, string? action = null)
    {
        _tryOpen = tryOpen;
        InitializeComponent();
        Theme.PrepareSensitiveWindow(this);
        if (heading != null)
            HeadingText.Text = heading;
        if (action != null)
            ActionButton.Content = action;
        ReasonText.Text = reason + " Enter the recovery code you saved at setup.";
        Loaded += (_, _) => CodeBox.Focus();
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        byte[] code;
        try
        {
            code = RecoveryCode.Parse(CodeBox.Text);
        }
        catch (FormatException error)
        {
            ShowError(error.Message);
            return;
        }

        try
        {
            if (_tryOpen(code))
                DialogResult = true;
            else
                ShowError("That code does not open this vault, or the vault file is damaged.");
        }
        finally
        {
            SecretBuffer.Wipe(code);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
