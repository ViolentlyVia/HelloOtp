using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace HelloOtp.Core;

public enum OtpAlgorithm : byte
{
    Sha1 = 1,
    Sha256 = 2,
    Sha512 = 3,
}

/// <summary>RFC 4226 one-time codes.</summary>
public static class Hotp
{
    public static string Compute(ReadOnlySpan<byte> secret, ulong counter, OtpAlgorithm algorithm = OtpAlgorithm.Sha1, int digits = 6)
    {
        if (digits is < 6 or > 8)
            throw new ArgumentOutOfRangeException(nameof(digits));

        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(message, counter);

        Span<byte> hash = stackalloc byte[64];
        var length = algorithm switch
        {
            OtpAlgorithm.Sha1 => HMACSHA1.HashData(secret, message, hash),
            OtpAlgorithm.Sha256 => HMACSHA256.HashData(secret, message, hash),
            OtpAlgorithm.Sha512 => HMACSHA512.HashData(secret, message, hash),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };

        var offset = hash[length - 1] & 0x0F;
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash.Slice(offset, 4)) & 0x7FFFFFFF;
        CryptographicOperations.ZeroMemory(hash);

        var modulus = digits switch { 6 => 1_000_000u, 7 => 10_000_000u, _ => 100_000_000u };
        return (value % modulus).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }
}

/// <summary>RFC 6238 time-based codes, the same calculation Google Authenticator performs.</summary>
public static class Totp
{
    public static long Step(long unixSeconds, int period) => unixSeconds / period;

    public static int SecondsRemaining(long unixSeconds, int period) => period - (int)(unixSeconds % period);

    public static string Compute(ReadOnlySpan<byte> secret, long unixSeconds, int period = 30, OtpAlgorithm algorithm = OtpAlgorithm.Sha1, int digits = 6)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unixSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        return Hotp.Compute(secret, (ulong)Step(unixSeconds, period), algorithm, digits);
    }
}
