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

## Key design

A random 256-bit vault key encrypts the accounts. It is stored twice:

- wrapped with a key derived by HKDF from the RSA signature that the Windows Hello key makes over a fixed challenge
- wrapped with a key derived by HKDF from a random 160-bit recovery code

Setup signs twice and refuses to continue if the signatures differ.

After a Hello check the vault key stays in memory, encrypted so only this process can read it. It is wiped when Windows locks, on sleep, on quit, or 12 hours after that check. Opening the panel does not extend the 12 hours.

## Zero connectivity

- The installer adds an outbound Windows Firewall block rule for `HelloOtp.exe`. This is the enforcement.
- A unit test fails if HelloOtp's own assemblies reference networking namespaces.
- There is no updater, telemetry or crash reporting.
- Windows Hello runs in a Windows system process, not in HelloOtp.
- NuGet is used only when building from source.

The firewall rule matches the exe's path. If you skip it with `-NoFirewall`, the app still has no network code, but Windows is not enforcing that.
