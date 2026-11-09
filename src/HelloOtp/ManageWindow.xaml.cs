using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using HelloOtp.Core;

namespace HelloOtp;

public partial class ManageWindow : Window
{
    private readonly UnlockedVault _vault;
    private readonly Action<byte[]> _save;
    private readonly ObservableCollection<OtpAccount> _accounts;

    internal ManageWindow(UnlockedVault vault, Action<byte[]> save)
    {
        _vault = vault;
        _save = save;
        InitializeComponent();
        Theme.PrepareSensitiveWindow(this);
        _accounts = new ObservableCollection<OtpAccount>(vault.Accounts);
        Accounts.ItemsSource = _accounts;
        Closed += (_, _) => _vault.Dispose();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OtpAccount account)
            return;
        var name = account.Issuer.Length > 0 ? account.Issuer : account.Label;
        var answer = MessageBox.Show(this,
            $"Remove {name} from the vault? After you save, its codes can only come back by importing it again. If sync is on, it is also removed from your other PCs.",
            "HelloOtp", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
            _accounts.Remove(account);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        foreach (var account in _vault.Accounts.Where(account => !_accounts.Contains(account)).ToList())
        {
            _vault.Remove(account);
        }
        _save(_vault.ToFileBytes());
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
