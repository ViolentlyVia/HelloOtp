# Threat model

## What HelloOtp protects

Two-factor secrets, at rest and while locked.

| Situation | Result |
|---|---|
| Someone copies `vault.bin` off the PC | They get AES-256-GCM ciphertext. The key needs the TPM-backed Windows Hello key or the recovery code. |
| A program running as you reads the vault while it is locked | Same. It needs a Windows Hello signature, which requires you to pass a prompt. |
| The vault file is altered | Decryption fails. Every byte is authenticated. |
| Someone walks up to the locked PC | Locking ends the Hello session, so codes need Windows Hello again. |
| Windows clipboard history or cloud clipboard | Copied codes are marked to be excluded from both, and cleared after 30 seconds. |
| Screenshots, screen sharing, Recall | The panel and the windows showing secrets are excluded from capture. |

## What it does not protect against

- **Someone using your PC while it is unlocked.** After a Hello check, one click shows every code until Windows locks or 12 hours pass. Lock Windows when you step away.
- **Malware already running as you after a Hello check.** Until Windows locks or 12 hours pass, the vault key is in HelloOtp's memory. It is encrypted with `CryptProtectMemory`, but code injected into the process could decrypt it. Decoded secrets are in memory only while the panel is open. Malware could also read the clipboard, or try to trick you into passing a Hello prompt.
- **An attacker with your recovery code and the vault file.** Keep the code offline.
- **A compromised Windows or firmware.**
- **Displayed codes and text you type.** Six-digit codes and anything typed in the import window, including a pasted secret, pass through .NET strings, which cannot be wiped. Decoded secrets are kept in pinned byte buffers and wiped.
- **Your export QR codes.** They contain every secret in plain form. Delete the images after importing.
- **The frozen screen while adding from a QR code.** The picture of your screen is held in memory until you finish selecting. It is never saved to disk, and the overlay is excluded from screen capture. HelloOtp wipes its own copy, but Windows graphics memory cannot be wiped.

## Sync

Sync is off until you choose a folder. Each PC then writes its own encrypted `.hotp` file there and reads the others. HelloOtp only reads and writes files. OneDrive, or Windows file sharing for a network folder, moves them.

| Situation | Result |
|---|---|
| The folder's provider or someone with access copies the files | They get AES-256-GCM ciphertext. Without the recovery code the vault key cannot be recovered. |
| Someone alters a file or adds their own | It fails authentication and is ignored. Only a holder of the vault key can make a valid file. |
| Someone restores an old copy of a file | Nothing is undone. Newer changes and deletions always win a merge. |

What sync changes:

- **Your recovery code matters more.** A sync file has no Windows Hello protection, because Hello keys cannot leave a PC. Anyone with the recovery code and access to the folder can read every account, from anywhere. Keep the code offline.
- **Deleted accounts can linger in old copies.** A deletion removes the secret from each PC's file once that PC syncs, but cloud version history may keep older encrypted copies. If a code leaks, change it at the service as well.
- **A retired PC's file stays in the folder** until you turn sync off on that PC or delete the file.
- **Merges trust the Windows clock.** With two conflicting changes, the later timestamp wins.

## Key design

A random 256-bit vault key encrypts the accounts. It is stored twice:

- wrapped with a key derived by HKDF from the RSA signature that the Windows Hello key makes over a fixed challenge
- wrapped with a key derived by HKDF from a random 160-bit recovery code

Setup signs twice and refuses to continue if the signatures differ.

After a Hello check the vault key stays in memory, encrypted so only this process can read it. It is wiped when Windows locks, on sleep, on quit, or 12 hours after that check. Opening the panel does not extend the 12 hours.

## Zero connectivity

- The installer adds an outbound Windows Firewall block rule for `HelloOtp.exe`. This is the enforcement.
- A unit test fails if HelloOtp's own assemblies reference networking namespaces.
- Sync adds no network code. It uses ordinary file reads and writes on a folder you choose, and the firewall rule stays on.
- There is no updater, telemetry or crash reporting.
- Windows Hello runs in a Windows system process, not in HelloOtp.
- NuGet is used only when building from source.

The firewall rule matches the exe's path. If you skip it with `-NoFirewall`, the app still has no network code, but Windows is not enforcing that.
