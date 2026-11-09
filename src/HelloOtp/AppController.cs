using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using HelloOtp.Core;
using Microsoft.Win32;

namespace HelloOtp;

/// <summary>Coordinates Windows Hello, the vault and the windows. Every unlock goes through here.</summary>
internal sealed class AppController
{
    public static readonly string DataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelloOtp");

    private const int ScreenSettleMilliseconds = 250;
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(3);
    private const long SyncRetryMilliseconds = 60_000;

    private readonly VaultStore _store = new(DataDirectory);
    private readonly SafeClipboard _clipboard = new();
    private readonly HelloSession _session = new();
    private readonly DispatcherTimer _sessionTimer = new() { Interval = HelloSession.Lifetime };
    private FloatingIcon? _icon;
    private TrayIcon? _tray;
    private CodePanel? _panel;
    private long _panelClosedAt = long.MinValue / 2;
    private bool _busy;
    private int _lockCount;
    private long _syncRetryAt;
    private string _syncStatus = "It has not synced since HelloOtp started. It syncs each time you open your codes.";

    public AppController()
    {
        // Wipes the key at the deadline instead of leaving it in memory until the next click.
        _sessionTimer.Tick += (_, _) => EndHelloSession();
    }

    public AppSettings Settings { get; } = AppSettings.Load(DataDirectory);

    public void Start()
    {
        _icon = new FloatingIcon(this);
        if (FloatingIconVisible)
            _icon.Show();
        _tray = new TrayIcon(this);
        _tray.Show();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void Stop()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        LockAll();
        _clipboard.ClearIfStillOurs();
        _tray?.Dispose();
    }

    public bool FloatingIconVisible => !Settings.HideFloatingIcon;

    /// <summary>Shows the floating lock icon or minimizes it to the tray. The choice survives restarts.</summary>
    public void SetFloatingIconVisible(bool visible)
    {
        Settings.HideFloatingIcon = !visible;
        SaveSettings();
        if (visible)
            _icon?.Show();
        else
            _icon?.Hide();
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

    private void BeginHelloSession(UnlockedVault vault)
    {
        _session.Begin(vault.CopyKey());
        _sessionTimer.Stop();
        if (_session.IsActive)
            _sessionTimer.Start();
    }

    private void EndHelloSession()
    {
        _sessionTimer.Stop();
        _session.End();
    }

    /// <summary>Forgets the Hello check and closes every window that can hold secrets. Each one wipes what it holds on close.</summary>
    public void LockAll()
    {
        _lockCount++;
        EndHelloSession();
        foreach (var window in Application.Current.Windows.OfType<Window>().ToList())
        {
            if (window != _icon)
                window.Close();
        }
    }

    public void IconClicked(IntPtr previousWindow)
    {
        var handle = Native.Handle(_icon!);
        OpenPanel(previousWindow, Native.GetRect(handle), Native.MonitorOf(handle).WorkArea);
    }

    public void TrayClicked(int x, int y)
    {
        // Pressing the tray icon takes focus from an open panel, which closes it. Without this
        // check, the end of that same click would open the panel again.
        var toggleWindow = System.Windows.Forms.SystemInformation.DoubleClickTime + 500;
        if (Environment.TickCount64 - _panelClosedAt < toggleWindow)
            return;

        OpenPanel(Native.PreviousAppWindow(), new PixelRect(x, y, x, y), Native.MonitorAt(x, y).WorkArea);
    }

    private async void OpenPanel(IntPtr previousWindow, PixelRect anchor, PixelRect workArea)
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
            _panel = new CodePanel(vault, previousWindow, code => _clipboard.SetText(code),
                () => AddFromScreen(previousWindow, anchor, workArea));
            _panel.Closed += (_, _) =>
            {
                _panel = null;
                _panelClosedAt = Environment.TickCount64;
            };
            _panel.ShowBeside(anchor, workArea);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Lets the user select a QR code on screen, adds its accounts, then returns to the panel.</summary>
    private async void AddFromScreen(IntPtr previousWindow, PixelRect anchor, PixelRect workArea)
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            await ScanAndAddAsync();
        }
        finally
        {
            _busy = false;
        }

        // Windows locking during the scan ends the session. Reopening then would raise a Hello prompt nobody asked for.
        if (_session.IsActive)
            OpenPanel(previousWindow, anchor, workArea);
    }

    private async Task ScanAndAddAsync()
    {
        while (true)
        {
            // Lets the panel or a message box leave the screen before it is frozen.
            await Task.Delay(ScreenSettleMilliseconds);

            var snip = await ScreenSnip.SelectAsync();
            if (snip == null)
                return;

            string? text;
            try
            {
                text = QrDecoder.DecodeSnip(snip.Pixels, snip.Width, snip.Height);
            }
            finally
            {
                SecretBuffer.Wipe(snip.Pixels);
            }

            if (text != null)
            {
                await AddScannedAsync(text);
                return;
            }

            var retry = MessageBox.Show("No QR code could be read in that area. Try again?",
                "HelloOtp", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (retry != MessageBoxResult.Yes)
                return;
        }
    }

    private async Task AddScannedAsync(string text)
    {
        MigrationBatch batch;
        try
        {
            batch = OtpLinks.Parse(text);
        }
        catch (FormatException error)
        {
            MessageBox.Show(error.Message, "HelloOtp", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var notes = new List<string>(batch.Skipped.Select(skipped => "Skipped " + skipped));
        if (batch.Accounts.Count > 0)
        {
            var total = batch.Accounts.Count;
            using var vault = await UnlockAsync();
            if (vault == null)
            {
                foreach (var account in batch.Accounts)
                    account.Dispose();
                return;
            }

            var added = AccountMerge.Merge(vault.Accounts, batch.Accounts);
            _store.Write(vault.ToFileBytes());
            PublishChanges();
            if (added < total)
                notes.Insert(0, added == 0 ? "That account is already in your vault." : $"Added {added}. {total - added} already in your vault.");
        }

        if (batch.BatchSize > 1)
            notes.Add($"Google split this export into {batch.BatchSize} QR codes. Press + to add the others.");
        if (notes.Count > 0)
            MessageBox.Show(string.Join("\n", notes), "HelloOtp", MessageBoxButton.OK, MessageBoxImage.Information);
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
                new ManageWindow(vault, bytes =>
                {
                    _store.Write(bytes);
                    PublishChanges();
                }).Show();
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
            PublishChanges();
            return added;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Opens the vault and brings in changes from other PCs.</summary>
    private async Task<UnlockedVault?> UnlockAsync()
    {
        var vault = await OpenVaultAsync();
        if (vault != null && !await SyncAsync(vault))
        {
            vault.Dispose();
            return null;
        }
        return vault;
    }

    /// <summary>
    /// Exchanges changes with the shared folder. A slow or missing folder never stops the codes from
    /// opening; it is retried a minute later. Returns false if Windows locked while this was waiting.
    /// </summary>
    private async Task<bool> SyncAsync(UnlockedVault vault)
    {
        var folder = Settings.SyncFolder;
        var ownName = Settings.SyncFile;
        if (folder == null || ownName == null || Environment.TickCount64 < _syncRetryAt)
            return true;

        var lockCount = _lockCount;
        try
        {
            var files = await Task.Run(() => SyncFolder.ReadAll(folder)).WaitAsync(SyncTimeout);
            if (lockCount != _lockCount)
                return false;

            // No awaits from reading the saved vault to writing it, so a save cannot slip between them.
            var outcome = VaultSync.Apply(vault, _store.Read(), files, ownName);
            if (outcome.Local != null)
                _store.Write(outcome.Local);
            if (outcome.Own != null)
                await Task.Run(() => SyncFolder.Write(folder, ownName, outcome.Own)).WaitAsync(SyncTimeout);

            _syncStatus = $"Last synced at {DateTime.Now:t}.";
            if (outcome.Unreadable > 0)
                _syncStatus += $" {outcome.Unreadable} file(s) in the folder could not be read. They may still be downloading, or belong to a different vault.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or VaultException or ArgumentException or NotSupportedException)
        {
            _syncRetryAt = Environment.TickCount64 + SyncRetryMilliseconds;
            _syncStatus = $"The last sync failed at {DateTime.Now:t}: {e.Message}";
        }
        return lockCount == _lockCount;
    }

    /// <summary>Sends a change just saved here to the folder. If that is not possible now, the next unlock sends it.</summary>
    private async void PublishChanges()
    {
        if (Settings.SyncFolder == null)
            return;
        var key = _session.TryCopyKey();
        if (key == null)
            return;

        UnlockedVault vault;
        try
        {
            vault = Vault.OpenWithKey(_store.Read(), key);
        }
        catch (Exception e) when (e is IOException or VaultException)
        {
            return;
        }
        finally
        {
            SecretBuffer.Wipe(key);
        }

        using (vault)
            await SyncAsync(vault);
    }

    public async void OpenSync()
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            if (Settings.SyncFolder != null)
                await ReviewSyncAsync(Settings.SyncFolder);
            else
                await TurnOnSyncAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ReviewSyncAsync(string folder)
    {
        var answer = MessageBox.Show(
            $"Syncing through:\n{folder}\n\n{_syncStatus}\n\nTurn sync off on this PC? Your accounts stay here, and this PC's file is removed from the folder.",
            "HelloOtp sync", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        var ownName = Settings.SyncFile;
        Settings.SyncFolder = null;
        Settings.SyncFile = null;
        SaveSettings();
        try
        {
            if (ownName != null)
                await Task.Run(() => SyncFolder.Delete(folder, ownName)).WaitAsync(SyncTimeout);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            MessageBox.Show($"Sync is off, but this PC's file could not be removed from the folder: {e.Message}",
                "HelloOtp sync", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task TurnOnSyncAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder all your PCs can reach" };
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (Directory.Exists(oneDrive))
            dialog.InitialDirectory = oneDrive;
        if (dialog.ShowDialog() != true)
            return;

        var folder = dialog.FolderName;
        List<SyncFile> files;
        try
        {
            files = await Task.Run(() => SyncFolder.ReadAll(folder)).WaitAsync(SyncTimeout);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            MessageBox.Show($"That folder could not be read: {e.Message}", "HelloOtp sync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (files.Count == 0)
        {
            // Saying yes on a second PC before the file arrives would start a separate vault.
            var answer = MessageBox.Show(
                "This folder has no synced vault yet. Start syncing this PC's vault here?\n\nIf your other PC already syncs to this folder, choose No and try again once its file has arrived.",
                "HelloOtp sync", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        if (!_store.Exists && files.Count == 0 && !await SetupAsync())
            return;

        UnlockedVault? local = null;
        if (_store.Exists)
        {
            local = await OpenVaultAsync();
            if (local == null)
                return;
        }

        using (local)
        {
            if (local != null && (files.Count == 0 || VaultSync.AnyOpensWith(local, files)))
            {
                await FinishTurningOnAsync(local, folder);
                return;
            }

            using var joined = await JoinAsync(files, local);
            if (joined != null)
                await FinishTurningOnAsync(joined, folder);
        }
    }

    /// <summary>Adopts the vault another PC put in the folder, keeping any accounts already on this PC.</summary>
    private async Task<UnlockedVault?> JoinAsync(List<SyncFile> files, UnlockedVault? local)
    {
        if (!await HelloKey.IsAvailableAsync())
        {
            Report(HelloStatus.Unavailable);
            return null;
        }

        var reason = local == null
            ? "This folder holds the vault from your other PC."
            : "This folder holds a different vault from the one on this PC. Your accounts here will be merged into it, and from now on this PC uses its recovery code.";
        UnlockedVault? joined = null;
        var window = new RecoveryWindow(reason, code =>
        {
            joined?.Dispose();
            joined = VaultSync.OpenWithRecovery(files, code);
            return joined != null;
        }, heading: "Join your synced vault", action: "Join");
        if (window.ShowDialog() != true || joined == null)
        {
            joined?.Dispose();
            return null;
        }

        var signature = await SignVerifiedAsync(HelloKeyMode.OpenOrCreate);
        if (signature == null)
        {
            joined.Dispose();
            return null;
        }

        try
        {
            // An open Manage window holds the old vault key, and saving would replace the joined vault.
            foreach (var manage in Application.Current.Windows.OfType<ManageWindow>().ToList())
                manage.Close();

            joined.RewrapHello(signature);
            if (local != null)
                VaultSync.Merge(joined, local);
            _store.Write(joined.ToFileBytes());
            BeginHelloSession(joined);
            return joined;
        }
        finally
        {
            SecretBuffer.Wipe(signature);
        }
    }

    private async Task FinishTurningOnAsync(UnlockedVault vault, string folder)
    {
        Settings.SyncFolder = folder;
        Settings.SyncFile = SyncFolder.NewFileName(Environment.MachineName);
        SaveSettings();
        _syncRetryAt = 0;
        await SyncAsync(vault);
        MessageBox.Show($"Sync is on, using:\n{folder}\n\n{_syncStatus}", "HelloOtp sync", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Opens the vault, asking for Windows Hello only if there is no live session from an earlier check.</summary>
    private async Task<UnlockedVault?> OpenVaultAsync()
    {
        var key = _session.TryCopyKey();
        if (key != null)
        {
            try
            {
                return Vault.OpenWithKey(_store.Read(), key);
            }
            catch (VaultException)
            {
                // The file no longer matches the remembered key, so fall through to a real check.
                EndHelloSession();
            }
            finally
            {
                SecretBuffer.Wipe(key);
            }
        }

        var vault = await UnlockWithHelloAsync();
        if (vault != null)
            BeginHelloSession(vault);
        return vault;
    }

    private async Task<UnlockedVault?> UnlockWithHelloAsync()
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
            BeginHelloSession(vault);
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
