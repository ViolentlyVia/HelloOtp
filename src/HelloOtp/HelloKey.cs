using System.Security.Cryptography;
using System.Text;
using HelloOtp.Core;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace HelloOtp;

internal enum HelloStatus
{
    Success,
    Cancelled,
    KeyMissing,
    Unavailable,
    Failed,
}

internal sealed record HelloResult(HelloStatus Status, byte[]? Signature = null);

internal enum HelloKeyMode
{
    /// <summary>Use the existing key. Report KeyMissing if there is none.</summary>
    OpenOnly,
    /// <summary>Use the existing key, or create one if there is none.</summary>
    OpenOrCreate,
    /// <summary>Replace any existing key with a new one.</summary>
    Replace,
}

/// <summary>
/// Signs a fixed challenge with a TPM-backed Windows Hello key. Each signature needs a Hello prompt,
/// and RSA PKCS#1 v1.5 signatures repeat exactly, so the signature can derive a stable vault key.
/// </summary>
internal static class HelloKey
{
    private const string KeyName = "HelloOtp.VaultKey.v1";
    private const string PromptWindowClass = "Credential Dialog Xaml Host";

    private static readonly byte[] Challenge = SHA256.HashData(Encoding.UTF8.GetBytes("HelloOtp vault unlock v1"));

    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            return await KeyCredentialManager.IsSupportedAsync();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static async Task<HelloResult> SignAsync(HelloKeyMode mode)
    {
        Native.AllowAnyForeground();
        using var cancel = new CancellationTokenSource();
        _ = BringPromptForwardAsync(cancel.Token);
        try
        {
            var opened = await OpenKeyAsync(mode);
            if (opened.Credential == null)
                return new HelloResult(opened.Status);

            var signed = await opened.Credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(Challenge));
            if (signed.Status != KeyCredentialStatus.Success)
                return new HelloResult(Map(signed.Status));

            CryptographicBuffer.CopyToByteArray(signed.Result, out byte[] managed);
            var signature = SecretBuffer.CopyOf(managed);
            SecretBuffer.Wipe(managed);
            return new HelloResult(HelloStatus.Success, signature);
        }
        catch (Exception)
        {
            return new HelloResult(HelloStatus.Failed);
        }
        finally
        {
            cancel.Cancel();
        }
    }

    private static async Task<(KeyCredential? Credential, HelloStatus Status)> OpenKeyAsync(HelloKeyMode mode)
    {
        if (mode == HelloKeyMode.Replace)
            return ToKey(await KeyCredentialManager.RequestCreateAsync(KeyName, KeyCredentialCreationOption.ReplaceExisting));

        var opened = await KeyCredentialManager.OpenAsync(KeyName);
        if (opened.Status == KeyCredentialStatus.NotFound && mode == HelloKeyMode.OpenOrCreate)
            return ToKey(await KeyCredentialManager.RequestCreateAsync(KeyName, KeyCredentialCreationOption.FailIfExists));
        return ToKey(opened);
    }

    private static (KeyCredential? Credential, HelloStatus Status) ToKey(KeyCredentialRetrievalResult result) =>
        result.Status == KeyCredentialStatus.Success
            ? (result.Credential, HelloStatus.Success)
            : (null, Map(result.Status));

    private static HelloStatus Map(KeyCredentialStatus status) => status switch
    {
        KeyCredentialStatus.Success => HelloStatus.Success,
        KeyCredentialStatus.UserCanceled => HelloStatus.Cancelled,
        KeyCredentialStatus.UserPrefersPassword => HelloStatus.Cancelled,
        KeyCredentialStatus.NotFound => HelloStatus.KeyMissing,
        _ => HelloStatus.Failed,
    };

    /// <summary>The Hello dialog can open behind other windows, so this brings it forward once it appears.</summary>
    private static async Task BringPromptForwardAsync(CancellationToken token)
    {
        try
        {
            for (var i = 0; i < 50; i++)
            {
                var prompt = Native.FindWindowByClass(PromptWindowClass);
                if (prompt != IntPtr.Zero)
                {
                    Native.SetForegroundWindow(prompt);
                    return;
                }
                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
