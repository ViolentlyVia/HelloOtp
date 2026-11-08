using HelloOtp.Core;

namespace HelloOtp;

/// <summary>
/// Remembers a passed Windows Hello check by holding the vault key, encrypted so only this process
/// can read it. Lasts until Windows locks or the lifetime runs out. Using it does not extend it.
/// </summary>
internal sealed class HelloSession
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private readonly Func<long> _nowMilliseconds;
    private byte[]? _protectedKey;
    private long _startedMilliseconds;

    public HelloSession(Func<long>? nowMilliseconds = null)
    {
        // A tick count, so changing the Windows clock cannot extend the session.
        _nowMilliseconds = nowMilliseconds ?? (() => Environment.TickCount64);
    }

    public bool IsActive => _protectedKey != null;

    /// <summary>Takes ownership of the key. If it cannot be protected, nothing is kept.</summary>
    public void Begin(byte[] key)
    {
        End();
        if (key.Length % Native.MemoryProtectionBlockSize != 0 || !Native.ProtectMemory(key))
        {
            SecretBuffer.Wipe(key);
            return;
        }
        _protectedKey = key;
        _startedMilliseconds = _nowMilliseconds();
    }

    /// <summary>A plain copy of the key for the caller to wipe, or null if a new Hello check is needed.</summary>
    public byte[]? TryCopyKey()
    {
        if (_protectedKey == null)
            return null;
        if (_nowMilliseconds() - _startedMilliseconds >= (long)Lifetime.TotalMilliseconds)
        {
            End();
            return null;
        }

        var copy = SecretBuffer.CopyOf(_protectedKey);
        if (Native.UnprotectMemory(copy))
            return copy;

        SecretBuffer.Wipe(copy);
        End();
        return null;
    }

    public void End()
    {
        SecretBuffer.Wipe(_protectedKey);
        _protectedKey = null;
    }
}
