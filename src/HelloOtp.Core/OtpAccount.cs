using System.Security.Cryptography;

namespace HelloOtp.Core;

/// <summary>One two-factor account. Takes ownership of the secret buffer and wipes it on dispose.</summary>
public sealed class OtpAccount : IDisposable
{
    public OtpAccount(string issuer, string label, byte[] secret, OtpAlgorithm algorithm = OtpAlgorithm.Sha1, int digits = 6, int period = 30)
    {
        if (secret.Length == 0)
            throw new FormatException("The secret is empty.");
        if (digits is < 6 or > 8)
            throw new FormatException("Only codes of 6 to 8 digits are supported.");
        if (period is < 1 or > 3600)
            throw new FormatException("The code period is not valid.");
        if (!Enum.IsDefined(algorithm))
            throw new FormatException("The hash algorithm is not supported.");

        _issuer = issuer;
        _label = label;
        Secret = secret;
        Algorithm = algorithm;
        Digits = digits;
        Period = period;
        ModifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private string _issuer;
    private string _label;

    public string Issuer
    {
        get => _issuer;
        set
        {
            if (value == _issuer)
                return;
            _issuer = value;
            ModifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    public string Label
    {
        get => _label;
        set
        {
            if (value == _label)
                return;
            _label = value;
            ModifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    public byte[] Secret { get; }
    public OtpAlgorithm Algorithm { get; }
    public int Digits { get; }
    public int Period { get; }

    /// <summary>When this account was added or renamed, in Unix milliseconds. Sync keeps the latest. Zero for accounts from before sync.</summary>
    public long ModifiedAt { get; set; }

    /// <summary>A hash naming this secret, so a deletion can be remembered without keeping the secret.</summary>
    public byte[] Identity()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("HelloOtp account identity v1"u8);
        hash.AppendData(Secret);
        hash.AppendData(new[] { (byte)Algorithm, (byte)Digits });
        hash.AppendData(BitConverter.GetBytes(Period));
        return hash.GetHashAndReset();
    }

    public string CodeAt(long unixSeconds) => Totp.Compute(Secret, unixSeconds, Period, Algorithm, Digits);

    public bool HasSameSecretAs(OtpAccount other) =>
        Secret.AsSpan().SequenceEqual(other.Secret)
        && Algorithm == other.Algorithm
        && Digits == other.Digits
        && Period == other.Period;

    public void Dispose() => SecretBuffer.Wipe(Secret);

    /// <summary>Splits "Issuer:label" names and removes an issuer prefix repeated in the label.</summary>
    internal static (string Issuer, string Label) NormalizeNames(string issuer, string name)
    {
        issuer = issuer.Trim();
        name = name.Trim();
        var colon = name.IndexOf(':');
        if (colon >= 0)
        {
            var prefix = name[..colon].Trim();
            var rest = name[(colon + 1)..].Trim();
            if (issuer.Length == 0)
                return (prefix, rest);
            if (string.Equals(issuer, prefix, StringComparison.OrdinalIgnoreCase))
                return (issuer, rest);
        }
        return (issuer, name);
    }
}

public static class AccountMerge
{
    /// <summary>Moves accounts not already present into the target, wiping any duplicates. Returns how many were added.</summary>
    public static int Merge(List<OtpAccount> target, IEnumerable<OtpAccount> incoming)
    {
        var added = 0;
        foreach (var account in incoming)
        {
            if (target.Any(existing => existing.HasSameSecretAs(account)))
            {
                account.Dispose();
                continue;
            }
            target.Add(account);
            added++;
        }
        return added;
    }
}
