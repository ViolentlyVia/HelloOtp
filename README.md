# HelloOtp

A small floating icon for Windows. Click it, pass Windows Hello, and click a two-factor code to copy it. Everything stays on your PC unless you turn on sync, which shares only encrypted files through a folder you choose.

## Install

Needs Windows 10 2004 or later with Windows Hello (PIN, fingerprint or face) set up.

Download `HelloOtp-vX.Y.Z-win-x64.zip` from [Releases](../../releases), extract it, and run this in the extracted folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

One admin prompt appears, for the firewall rule that blocks the app from the network. Installs to `%LOCALAPPDATA%\Programs\HelloOtp` and starts at sign-in. Use `-NoFirewall` or `-NoStartup` to skip those steps.

To build from source instead, install the .NET 10 SDK and run `install\install.ps1` from a copy of this repository.

## First run

1. Click the icon. Windows Hello confirms a few times while the vault is created.
2. Save the recovery code. It is shown once, and it is the only way back in after a Windows Hello reset or a Windows reinstall.
3. In Google Authenticator, choose Transfer accounts, then Export, and capture the QR codes. Larger exports span several QR codes.
4. In the import window, choose the images or paste a screenshot, then save.
5. Delete the QR images. They contain your secrets.

## Daily use

| Action | Result |
|---|---|
| Click the icon | The code panel. Windows Hello asks first if Windows has locked or slept since the last check, or that check is 12 hours old. |
| Click a code, or press Enter on the selected one | Copies it, closes the panel, and returns to your previous window |
| Type in the panel | Filters accounts. Up and Down move the selection. |
| Press + in the panel | Freezes the screen so you can drag around a QR code. Its account is added to the vault. |
| Drag the icon | Moves it. It remembers the monitor. |
| Right-click the icon | Minimize to tray, import, add, manage, start with Windows, quit |
| Click the tray icon | Opens the code panel next to the tray |
| Double-click the tray icon | Shows the floating lock icon, or minimizes it to the tray. It stays that way after a restart. |
| Right-click the tray icon | The same actions as the floating icon's menu |

The panel locks on Esc, clicking away, 60 seconds idle, sleep, or locking Windows. The clipboard clears after 30 seconds.

## Sync between PCs

1. On the PC that has your accounts, right-click the icon, choose **Sync between PCs...**, and pick a folder your PCs can all reach, such as one in OneDrive or on a network share.
2. Wait for the file to reach the other PC. Install HelloOtp there and choose the same option and folder.
3. Enter your recovery code and pass Windows Hello. If that PC already had accounts, they are merged in, and it uses the first PC's recovery code from then on.

After that, each PC syncs when you open your codes and after you add or remove an account, with no extra Windows Hello prompts. Removing an account removes it everywhere. If the folder is unavailable, your codes still open and sync tries again later.

Choose the same menu item to see the last sync or turn sync off on that PC.

## Recovery

If Windows Hello can no longer open the vault, HelloOtp asks for the recovery code and binds the vault to a new Hello key. The previous vault version is kept as `vault.bak` in `%LOCALAPPDATA%\HelloOtp`.

Back up `vault.bin` together with your recovery code to move to another PC.

## Limits

Time-based codes only. Counter-based (HOTP) accounts are skipped with a warning. Codes depend on the Windows clock being correct.

See [docs/threat-model.md](docs/threat-model.md) for what this does and does not protect against.

## Development

```powershell
dotnet test
```

`install\build-release.ps1` builds the release zip from committed code.

To remove the app, run `uninstall.ps1` from the release zip or the `install` folder. The vault is kept unless you pass `-DeleteVault`.
