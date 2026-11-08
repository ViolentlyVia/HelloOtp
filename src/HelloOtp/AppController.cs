using System.IO;
using System.Security.Cryptography;
using System.Windows;
using HelloOtp.Core;
using Microsoft.Win32;

namespace HelloOtp;

/// <summary>Coordinates Windows Hello, the vault and the windows. Every unlock goes through here.</summary>
internal sealed class AppController
{
    public static readonly string DataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelloOtp");

    private readonly VaultStore _store = new(DataDirectory);
    private readonly SafeClipboard _clipboard = new();
    private FloatingIcon? _icon;
    private CodePanel? _panel;
    private bool _busy;

    public AppSettings Settings { get; } = AppSettings.Load(DataDirectory);

    public void Start()
    {
        _icon = new FloatingIcon(this);
        _icon.Show();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void Stop()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        LockAll();
        _clipboard.ClearIfStillOurs();
    }

    public void SaveSettings() => Settings.Save(DataDirectory);

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            Application.Current.Dispatcher.BeginInvoke(LockAll);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            Application.Current.Dispatcher.BeginInvoke(LockAll);
    }

    /// <summary>Closes every window that can hold secrets. Each one wipes what it holds on close.</summary>
    public void LockAll()
    {
        foreach (var window in Application.Current.Windows.OfType<Window>().ToList())
        {
            if (window != _icon)
                window.Close();
        }
    }

    public async void IconClicked(IntPtr previousWindow)
    {
        if (_panel != null)
        {
            _panel.Close();
            return;
        }
        if (_busy)
            return;

        _busy = true;
        try
        {
            if (!_store.Exists)
            {
                if (await SetupAsync())
                    new ImportWindow(this, manualFocus: false).Show();
                return;
            }

            var vault = await UnlockAsync();
            if (vault == null)
                return;

            vault.ForgetKey();
            _panel = new CodePanel(vault, previousWindow, code => _clipboard.SetText(code));
            _panel.Closed += (_, _) => _panel = null;
            _panel.ShowBeside(Native.Handle(_icon!));
        }
        finally
        {
            _busy = false;
        }
    }

    public async void OpenImport(bool manualFocus)
    {
        var existing = Application.Current.Windows.OfType<ImportWindow>().FirstOrDefault();
        if (existing != null)
        {
            existing.Activate();
            return;
        }
        if (_busy)
            return;

        _busy = true;
        try
        {
            if (_store.Exists || await SetupAsync())
                new ImportWindow(this, manualFocus).Show();
        }
        finally
        {
            _busy = false;
        }
    }

    public async void OpenManage()
    {
        var existing = Application.Current.Windows.OfType<ManageWindow>().FirstOrDefault();
        if (existing != null)
        {
            existing.Activate();
            return;
        }
        if (_busy || !_store.Exists)
            return;

        _busy = true;
        try
        {
            var vault = await UnlockAsync();
            if (vault != null)
                new ManageWindow(vault, bytes => _store.Write(bytes)).Show();
        }
        finally
        {
            _busy = false;
        }
    }

    public void CopySensitive(string text) => _clipboard.SetText(text);

    /// <summary>Unlocks with Hello, merges the accounts and saves. Takes ownership of the accounts on success.</summary>
    public async Task<int?> AddAccountsAsync(List<OtpAccount> accounts)
    {
        if (_busy)
            return null;

        _busy = true;
        try
        {
            using var vault = await UnlockAsync();
            if (vault == null)
                return null;
            var added = AccountMerge.Merge(vault.Accounts, accounts);
            _store.Write(vault.ToFileBytes());
            return added;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<UnlockedVault?> UnlockAsync()
    {
        var result = await HelloKey.SignAsync(HelloKeyMode.OpenOnly);
        try
        {
            switch (result.Status)
            {
                case HelloStatus.Success:
                    try
                    {
                        return Vault.OpenWithHello(_store.Read(), result.Signature);
                    }
                    catch (VaultException)
                    {
                        return await RecoverAsync("Windows Hello could not open your vault. This can happen after Windows Hello changes.");
                    }
                case HelloStatus.KeyMissing:
                    return await RecoverAsync("The Windows Hello key for your vault no longer exists. This happens after a Windows Hello reset or a Windows reinstall.");
                default:
                    Report(result.Status);
                    return null;
            }
        }
        finally
        {
            SecretBuffer.Wipe(result.Signature);
        }
    }

    private async Task<bool> SetupAsync()
    {
        if (!await HelloKey.IsAvailableAsync())
        {
            Report(HelloStatus.Unavailable);
            return false;
        }

        MessageBox.Show(
            "HelloOtp will now create its vault. Windows Hello may ask you to confirm a few times during this one-time setup.",
            "HelloOtp setup", MessageBoxButton.OK, MessageBoxImage.Information);

        var signature = await SignVerifiedAsync(HelloKeyMode.OpenOrCreate);
        if (signature == null)
            return false;

        var recovery = RecoveryCode.Generate();
        try
        {
            if (new SetupWindow(RecoveryCode.Format(recovery), CopySensitive).ShowDialog() != true)
                return false;
            using var vault = Vault.Create(signature, recovery);
            _store.Write(vault.ToFileBytes());
            return true;
        }
        finally
        {
            SecretBuffer.Wipe(signature);
            SecretBuffer.Wipe(recovery);
        }
    }

    private async Task<UnlockedVault?> RecoverAsync(string reason)
    {
        var file = _store.Read();
        UnlockedVault? vault = null;
        var window = new RecoveryWindow(reason, code =>
        {
            try
            {
                vault = Vault.OpenWithRecovery(file, code);
                return true;
            }
            catch (VaultException)
            {
                return false;
            }
        });
        if (window.ShowDialog() != true || vault == null)
        {
            vault?.Dispose();
            return null;
        }

        var signature = await SignVerifiedAsync(HelloKeyMode.Replace);
        if (signature == null)
        {
            vault.Dispose();
            return null;
        }

        try
        {
            vault.RewrapHello(signature);
            _store.Write(vault.ToFileBytes());
            return vault;
        }
        finally
        {
            SecretBuffer.Wipe(signature);
        }
    }

    /// <summary>Signs twice and compares, because a vault must never be bound to a signature that changes.</summary>
    private static async Task<byte[]?> SignVerifiedAsync(HelloKeyMode mode)
    {
        var first = await HelloKey.SignAsync(mode);
        if (first.Status != HelloStatus.Success)
        {
            Report(first.Status);
            return null;
        }

        var second = await HelloKey.SignAsync(HelloKeyMode.OpenOnly);
        try
        {
            if (second.Status != HelloStatus.Success)
            {
                Report(second.Status);
                SecretBuffer.Wipe(first.Signature);
                return null;
            }
            if (!CryptographicOperations.FixedTimeEquals(first.Signature, second.Signature))
            {
                MessageBox.Show(
                    "Windows Hello returned a different signature each time on this PC, so a vault key cannot be derived from it. No vault was changed.",
                    "HelloOtp", MessageBoxButton.OK, MessageBoxImage.Error);
                SecretBuffer.Wipe(first.Signature);
                return null;
            }
            return first.Signature;
        }
        finally
        {
            SecretBuffer.Wipe(second.Signature);
        }
    }

    private static void Report(HelloStatus status)
    {
        switch (status)
        {
            case HelloStatus.Unavailable:
                MessageBox.Show(
                    "Windows Hello is not set up. Add a PIN, fingerprint or face in Settings, Accounts, Sign-in options, then try again.",
                    "HelloOtp", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            case HelloStatus.Failed:
            case HelloStatus.KeyMissing:
                MessageBox.Show("Windows Hello could not complete the request. Nothing was unlocked.",
                    "HelloOtp", MessageBoxButton.OK, MessageBoxImage.Error);
                break;
        }
    }
}
