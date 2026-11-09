using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HelloOtp.Core;

public sealed class VaultException : Exception
{
    public VaultException(string message) : base(message) { }
}

/// <summary>
/// The encrypted vault file. A random vault key encrypts the accounts with AES-256-GCM. That key is
/// stored twice, wrapped by a key derived from the Windows Hello signature and by one derived from
/// the recovery code.
///
/// Layout: "HOTP", version, salt(32), hello wrap(60), recovery wrap(60), body nonce(12), body tag(16), body.
/// A sync file has the same layout with an empty Hello wrap, because each PC has its own Hello key.
/// Version 2 adds a modified time to each account and a list of deleted accounts.
/// </summary>
public static class Vault
{
    internal const int KeySize = 32;
    internal const int NonceSize = 12;
    internal const int TagSize = 16;
    internal const int WrapSize = NonceSize + TagSize + KeySize;
    internal const int SaltSize = 32;
    internal const byte Version = 2;
    private const byte FirstVersion = 1;
    internal const int HeaderSize = 5 + SaltSize + WrapSize + WrapSize;
    internal static ReadOnlySpan<byte> Magic => "HOTP"u8;
    internal static ReadOnlySpan<byte> HelloInfo => "HelloOtp hello wrap v1"u8;
    internal static ReadOnlySpan<byte> RecoveryInfo => "HelloOtp recovery wrap v1"u8;

    public static UnlockedVault Create(ReadOnlySpan<byte> helloSignature, ReadOnlySpan<byte> recoveryCode)
    {
        var key = SecretBuffer.Allocate(KeySize);
        RandomNumberGenerator.Fill(key);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        return new UnlockedVault(
            key,
            salt,
            Wrap(key, helloSignature, salt, HelloInfo),
            Wrap(key, recoveryCode, salt, RecoveryInfo),
            new List<OtpAccount>(),
            new List<Tombstone>());
    }

    public static UnlockedVault OpenWithHello(ReadOnlySpan<byte> file, ReadOnlySpan<byte> helloSignature) =>
        Open(file, helloSignature, HelloInfo, helloWrap: true);

    public static UnlockedVault OpenWithRecovery(ReadOnlySpan<byte> file, ReadOnlySpan<byte> recoveryCode) =>
        Open(file, recoveryCode, RecoveryInfo, helloWrap: false);

    /// <summary>Opens with a vault key kept from an earlier unlock.</summary>
    public static UnlockedVault OpenWithKey(ReadOnlySpan<byte> file, ReadOnlySpan<byte> key)
    {
        ReadHeader(file, out var salt, out var hello, out var recovery);
        return Decrypt(file, SecretBuffer.CopyOf(key), salt, hello, recovery);
    }

    private static UnlockedVault Open(ReadOnlySpan<byte> file, ReadOnlySpan<byte> material, ReadOnlySpan<byte> info, bool helloWrap)
    {
        ReadHeader(file, out var salt, out var hello, out var recovery);
        var key = Unwrap(helloWrap ? hello : recovery, material, salt, info);
        return Decrypt(file, key, salt, hello, recovery);
    }

    private static void ReadHeader(ReadOnlySpan<byte> file, out byte[] salt, out byte[] hello, out byte[] recovery)
    {
        if (file.Length < HeaderSize + NonceSize + TagSize || !file[..4].SequenceEqual(Magic))
            throw new VaultException("The vault file is not valid.");
        if (file[4] is not (FirstVersion or Version))
            throw new VaultException("The vault file was made by a different version of HelloOtp.");

        salt = file.Slice(5, SaltSize).ToArray();
        hello = file.Slice(5 + SaltSize, WrapSize).ToArray();
        recovery = file.Slice(5 + SaltSize + WrapSize, WrapSize).ToArray();
    }

    /// <summary>Takes ownership of the key, wiping it on failure.</summary>
    private static UnlockedVault Decrypt(ReadOnlySpan<byte> file, byte[] key, byte[] salt, byte[] hello, byte[] recovery)
    {
        var plain = SecretBuffer.Allocate(file.Length - HeaderSize - NonceSize - TagSize);
        try
        {
            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(
                    file.Slice(HeaderSize, NonceSize),
                    file[(HeaderSize + NonceSize + TagSize)..],
                    file.Slice(HeaderSize + NonceSize, TagSize),
                    plain,
                    file[..HeaderSize]);
            }
            var (accounts, tombstones) = ReadBody(plain, file[4]);
            return new UnlockedVault(key, salt, hello, recovery, accounts, tombstones);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or IndexOutOfRangeException)
        {
            SecretBuffer.Wipe(key);
            throw new VaultException("The vault file is damaged or has been altered.");
        }
        finally
        {
            SecretBuffer.Wipe(plain);
        }
    }

    internal static byte[] Wrap(ReadOnlySpan<byte> key, ReadOnlySpan<byte> material, byte[] salt, ReadOnlySpan<byte> info)
    {
        var kek = DeriveKek(material, salt, info);
        try
        {
            var wrap = new byte[WrapSize];
            var nonce = wrap.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(kek, TagSize);
            aes.Encrypt(nonce, key, wrap.AsSpan(NonceSize + TagSize), wrap.AsSpan(NonceSize, TagSize), WrapAad(salt));
            return wrap;
        }
        finally
        {
            SecretBuffer.Wipe(kek);
        }
    }

    private static byte[] Unwrap(byte[] wrap, ReadOnlySpan<byte> material, byte[] salt, ReadOnlySpan<byte> info)
    {
        var kek = DeriveKek(material, salt, info);
        var key = SecretBuffer.Allocate(KeySize);
        try
        {
            using var aes = new AesGcm(kek, TagSize);
            aes.Decrypt(wrap.AsSpan(0, NonceSize), wrap.AsSpan(NonceSize + TagSize), wrap.AsSpan(NonceSize, TagSize), key, WrapAad(salt));
            return key;
        }
        catch (CryptographicException)
        {
            SecretBuffer.Wipe(key);
            throw new VaultException("The key does not open this vault.");
        }
        finally
        {
            SecretBuffer.Wipe(kek);
        }
    }

    private static byte[] DeriveKek(ReadOnlySpan<byte> material, byte[] salt, ReadOnlySpan<byte> info)
    {
        var kek = SecretBuffer.Allocate(KeySize);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, material, kek, salt, info);
        return kek;
    }

    private static byte[] WrapAad(byte[] salt)
    {
        // Always the first version: wraps made before later format changes must keep opening.
        var aad = new byte[5 + SaltSize];
        Magic.CopyTo(aad);
        aad[4] = FirstVersion;
        salt.CopyTo(aad, 5);
        return aad;
    }

    private static (List<OtpAccount> Accounts, List<Tombstone> Tombstones) ReadBody(ReadOnlySpan<byte> plain, byte version)
    {
        var accounts = new List<OtpAccount>();
        var tombstones = new List<Tombstone>();
        var position = 0;
        var count = ReadInt(plain, ref position);
        for (var i = 0; i < count; i++)
        {
            var issuer = Encoding.UTF8.GetString(ReadBlock(plain, ref position));
            var label = Encoding.UTF8.GetString(ReadBlock(plain, ref position));
            var secret = SecretBuffer.CopyOf(ReadBlock(plain, ref position));
            var algorithm = (OtpAlgorithm)plain[position++];
            var digits = (int)plain[position++];
            var period = ReadInt(plain, ref position);
            var account = new OtpAccount(issuer, label, secret, algorithm, digits, period) { ModifiedAt = 0 };
            if (version >= 2)
                account.ModifiedAt = ReadLong(plain, ref position);
            accounts.Add(account);
        }

        if (version >= 2)
        {
            var deleted = ReadInt(plain, ref position);
            for (var i = 0; i < deleted; i++)
            {
                var id = plain.Slice(position, Tombstone.IdSize).ToArray();
                position += Tombstone.IdSize;
                tombstones.Add(new Tombstone(id, ReadLong(plain, ref position)));
            }
        }
        return (accounts, tombstones);
    }

    private static long ReadLong(ReadOnlySpan<byte> data, ref int position)
    {
        var value = BinaryPrimitives.ReadInt64LittleEndian(data[position..]);
        position += 8;
        return value;
    }

    private static int ReadInt(ReadOnlySpan<byte> data, ref int position)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
        position += 4;
        return value;
    }

    private static ReadOnlySpan<byte> ReadBlock(ReadOnlySpan<byte> data, ref int position)
    {
        var length = ReadInt(data, ref position);
        var block = data.Slice(position, length);
        position += length;
        return block;
    }
}

/// <summary>Remembers that an account was deleted, so sync does not bring it back. Holds a hash, never the secret.</summary>
public sealed class Tombstone
{
    internal const int IdSize = 32;

    public Tombstone(byte[] id, long deletedAt)
    {
        Id = id;
        DeletedAt = deletedAt;
    }

    public byte[] Id { get; }
    public long DeletedAt { get; set; }
}

/// <summary>A decrypted vault. Dispose wipes the key and every secret.</summary>
public sealed class UnlockedVault : IDisposable
{
    private byte[]? _key;
    private readonly byte[] _salt;
    private byte[] _helloWrap;
    private readonly byte[] _recoveryWrap;

    internal UnlockedVault(byte[] key, byte[] salt, byte[] helloWrap, byte[] recoveryWrap, List<OtpAccount> accounts, List<Tombstone> tombstones)
    {
        _key = key;
        _salt = salt;
        _helloWrap = helloWrap;
        _recoveryWrap = recoveryWrap;
        Accounts = accounts;
        Tombstones = tombstones;
    }

    public List<OtpAccount> Accounts { get; }

    public List<Tombstone> Tombstones { get; }

    /// <summary>Removes and wipes an account, remembering the deletion so it reaches other PCs.</summary>
    public void Remove(OtpAccount account, long? deletedAt = null)
    {
        Accounts.Remove(account);
        var id = account.Identity();
        var time = deletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var existing = Tombstones.FirstOrDefault(tombstone => tombstone.Id.AsSpan().SequenceEqual(id));
        if (existing == null)
            Tombstones.Add(new Tombstone(id, time));
        else
            existing.DeletedAt = Math.Max(existing.DeletedAt, time);
        account.Dispose();
    }

    /// <summary>A pinned copy of the vault key, for the caller to protect and wipe.</summary>
    public byte[] CopyKey()
    {
        if (_key == null)
            throw new InvalidOperationException("The vault key has been wiped.");
        return SecretBuffer.CopyOf(_key);
    }

    /// <summary>Wipes the vault key. The accounts stay readable, but the vault can no longer be saved.</summary>
    public void ForgetKey()
    {
        SecretBuffer.Wipe(_key);
        _key = null;
    }

    /// <summary>After a Windows Hello reset, binds the vault to the new Hello key.</summary>
    public void RewrapHello(ReadOnlySpan<byte> helloSignature)
    {
        if (_key == null)
            throw new InvalidOperationException("The vault key has been wiped.");
        _helloWrap = Vault.Wrap(_key, helloSignature, _salt, Vault.HelloInfo);
    }

    public byte[] ToFileBytes() => Serialize(_helloWrap);

    /// <summary>The form written to the shared folder. Opens with the vault key or the recovery code, never with Hello.</summary>
    public byte[] ToSyncBytes() => Serialize(new byte[Vault.WrapSize]);

    private byte[] Serialize(byte[] helloWrap)
    {
        if (_key == null)
            throw new InvalidOperationException("The vault key has been wiped.");

        var plain = WriteAccounts();
        try
        {
            var file = new byte[Vault.HeaderSize + Vault.NonceSize + Vault.TagSize + plain.Length];
            Vault.Magic.CopyTo(file);
            file[4] = Vault.Version;
            _salt.CopyTo(file, 5);
            helloWrap.CopyTo(file, 5 + Vault.SaltSize);
            _recoveryWrap.CopyTo(file, 5 + Vault.SaltSize + Vault.WrapSize);

            var nonce = file.AsSpan(Vault.HeaderSize, Vault.NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(_key, Vault.TagSize);
            aes.Encrypt(
                nonce,
                plain,
                file.AsSpan(Vault.HeaderSize + Vault.NonceSize + Vault.TagSize),
                file.AsSpan(Vault.HeaderSize + Vault.NonceSize, Vault.TagSize),
                file.AsSpan(0, Vault.HeaderSize));
            return file;
        }
        finally
        {
            SecretBuffer.Wipe(plain);
        }
    }

    private byte[] WriteAccounts()
    {
        var size = 4;
        foreach (var account in Accounts)
        {
            size += 4 + Encoding.UTF8.GetByteCount(account.Issuer);
            size += 4 + Encoding.UTF8.GetByteCount(account.Label);
            size += 4 + account.Secret.Length + 2 + 4 + 8;
        }
        size += 4 + Tombstones.Count * (Tombstone.IdSize + 8);

        var plain = SecretBuffer.Allocate(size);
        var position = 0;
        WriteInt(plain, ref position, Accounts.Count);
        foreach (var account in Accounts)
        {
            WriteBlock(plain, ref position, Encoding.UTF8.GetBytes(account.Issuer));
            WriteBlock(plain, ref position, Encoding.UTF8.GetBytes(account.Label));
            WriteBlock(plain, ref position, account.Secret);
            plain[position++] = (byte)account.Algorithm;
            plain[position++] = (byte)account.Digits;
            WriteInt(plain, ref position, account.Period);
            WriteLong(plain, ref position, account.ModifiedAt);
        }

        WriteInt(plain, ref position, Tombstones.Count);
        foreach (var tombstone in Tombstones)
        {
            tombstone.Id.CopyTo(plain, position);
            position += Tombstone.IdSize;
            WriteLong(plain, ref position, tombstone.DeletedAt);
        }
        return plain;
    }

    private static void WriteLong(Span<byte> data, ref int position, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(data[position..], value);
        position += 8;
    }

    private static void WriteInt(Span<byte> data, ref int position, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(data[position..], value);
        position += 4;
    }

    private static void WriteBlock(Span<byte> data, ref int position, ReadOnlySpan<byte> block)
    {
        WriteInt(data, ref position, block.Length);
        block.CopyTo(data[position..]);
        position += block.Length;
    }

    public void Dispose()
    {
        ForgetKey();
        foreach (var account in Accounts)
            account.Dispose();
    }
}
