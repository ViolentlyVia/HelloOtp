using System.Security.Cryptography;

namespace HelloOtp.Core;

/// <summary>Byte buffers for secrets. Pinned, so the garbage collector cannot leave copies behind.</summary>
public static class SecretBuffer
{
    public static byte[] Allocate(int length) => GC.AllocateUninitializedArray<byte>(length, pinned: true);

    public static byte[] CopyOf(ReadOnlySpan<byte> source)
    {
        var copy = Allocate(source.Length);
        source.CopyTo(copy);
        return copy;
    }

    public static void Wipe(byte[]? buffer)
    {
        if (buffer != null)
            CryptographicOperations.ZeroMemory(buffer);
    }
}
