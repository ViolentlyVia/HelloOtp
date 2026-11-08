using System.Windows;

namespace HelloOtp;

public partial class SetupWindow : Window
{
    private readonly Action<string> _copy;

    internal SetupWindow(string recoveryCode, Action<string> copy)
    {
        _copy = copy;
        InitializeComponent();
        Theme.PrepareSensitiveWindow(this);
        CodeBox.Text = recoveryCode;
    }

    private void OnCopy(object sender, RoutedEventArgs e) => _copy(CodeBox.Text);

    private void OnSavedChanged(object sender, RoutedEventArgs e) => ContinueButton.IsEnabled = SavedBox.IsChecked == true;

    private void OnContinue(object sender, RoutedEventArgs e) => DialogResult = true;
}
