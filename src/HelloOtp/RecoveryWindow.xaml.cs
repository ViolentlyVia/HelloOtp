using System.Windows;
using HelloOtp.Core;

namespace HelloOtp;

public partial class RecoveryWindow : Window
{
    private readonly Func<byte[], bool> _tryOpen;

    internal RecoveryWindow(string reason, Func<byte[], bool> tryOpen)
    {
        _tryOpen = tryOpen;
        InitializeComponent();
        Theme.PrepareSensitiveWindow(this);
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
