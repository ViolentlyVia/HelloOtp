# HelloOtp

A small floating icon for Windows. Click it, pass Windows Hello, and click a two-factor code to copy it. Everything stays on your PC.

## Install

Needs the .NET 10 SDK to build, and Windows Hello (PIN, fingerprint or face) set up.

```powershell
powershell -ExecutionPolicy Bypass -File install\install.ps1
```

One admin prompt appears, for the firewall rule that blocks the app from the network. Installs to `%LOCALAPPDATA%\Programs\HelloOtp` and starts at sign-in. Use `-NoFirewall` or `-NoStartup` to skip those steps.

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
| Drag the icon | Moves it. It remembers the monitor. |
| Right-click the icon | Import, add, manage, start with Windows, quit |

The panel locks on Esc, clicking away, 60 seconds idle, sleep, or locking Windows. The clipboard clears after 30 seconds.

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

To remove the app, run `install\uninstall.ps1`. The vault is kept unless you pass `-DeleteVault`.
