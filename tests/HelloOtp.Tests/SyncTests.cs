using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HelloOtp.Core;
using Xunit;

namespace HelloOtp.Tests;

public sealed class SyncTests : IDisposable
{
    private static readonly byte[] Recovery = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
    private static readonly byte[] SecretA = Encoding.ASCII.GetBytes("aaaaaaaaaaaaaaaaaaaa");
    private static readonly byte[] SecretB = Encoding.ASCII.GetBytes("bbbbbbbbbbbbbbbbbbbb");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HelloOtpSync-" + Guid.NewGuid().ToString("N"));

    public SyncTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static OtpAccount Account(byte[] secret, string issuer, long modifiedAt = 1000) =>
        new(issuer, "me", SecretBuffer.CopyOf(secret)) { ModifiedAt = modifiedAt };

    /// <summary>One simulated PC: its own Hello key, its saved vault and its own file in the folder.</summary>
    private sealed class Pc
    {
        private readonly byte[] _signature;
        private readonly string _folder;
        private readonly string _own;

        public Pc(string name, byte[] localFile, byte[] signature, string folder)
        {
            Local = localFile;
            _signature = signature;
            _folder = folder;
            _own = name + SyncFolder.Extension;
        }

        public byte[] Local { get; private set; }

        public SyncOutcome Sync()
        {
            using var vault = Open();
            var outcome = VaultSync.Apply(vault, Local, SyncFolder.ReadAll(_folder), _own);
            if (outcome.Local != null)
                Local = outcome.Local;
            if (outcome.Own != null)
                SyncFolder.Write(_folder, _own, outcome.Own);
            return outcome;
        }

        public void Change(Action<UnlockedVault> change)
        {
            using var vault = Open();
            change(vault);
            Local = vault.ToFileBytes();
        }

        public UnlockedVault Open() => Vault.OpenWithHello(Local, _signature);

        public string[] Issuers()
        {
            using var vault = Open();
            return vault.Accounts.Select(account => account.Issuer).OrderBy(issuer => issuer).ToArray();
        }
    }

    private static byte[] Signature(byte fill) => Enumerable.Repeat(fill, 256).ToArray();

    /// <summary>A desktop that published first, and a laptop that joined with the recovery code.</summary>
    private (Pc Desktop, Pc Laptop) TwoPcs(params OtpAccount[] accounts)
    {
        byte[] file;
        using (var vault = Vault.Create(Signature(1), Recovery))
        {
            vault.Accounts.AddRange(accounts);
            file = vault.ToFileBytes();
        }

        var desktop = new Pc("desktop", file, Signature(1), _folder);
        desktop.Sync();

        byte[] joined;
        using (var vault = VaultSync.OpenWithRecovery(SyncFolder.ReadAll(_folder), Recovery)!)
        {
            vault.RewrapHello(Signature(2));
            joined = vault.ToFileBytes();
        }
        var laptop = new Pc("laptop", joined, Signature(2), _folder);
        laptop.Sync();
        return (desktop, laptop);
    }

    [Fact]
    public void LaptopJoinsWithRecoveryCodeAndItsOwnHelloKey()
    {
        var (desktop, laptop) = TwoPcs(Account(SecretA, "Alpha"));
        Assert.Equal(new[] { "Alpha" }, laptop.Issuers());
        Assert.Equal(new[] { "Alpha" }, desktop.Issuers());
        Assert.Throws<VaultException>(() => Vault.OpenWithHello(laptop.Local, Signature(1)));
    }

    [Fact]
    public void SyncFileDoesNotOpenWithHello()
    {
        TwoPcs(Account(SecretA, "Alpha"));
        var file = SyncFolder.ReadAll(_folder).First().Content;
        Assert.Throws<VaultException>(() => Vault.OpenWithHello(file, Signature(1)));
        Assert.Throws<VaultException>(() => Vault.OpenWithHello(file, new byte[256]));
        Assert.DoesNotContain("Alpha", Encoding.Latin1.GetString(file));
    }

    [Fact]
    public void WrongRecoveryCodeJoinsNothing()
    {
        TwoPcs(Account(SecretA, "Alpha"));
        Assert.Null(VaultSync.OpenWithRecovery(SyncFolder.ReadAll(_folder), new byte[20]));
    }

    [Fact]
    public void AddedAccountReachesTheOtherPc()
    {
        var (desktop, laptop) = TwoPcs(Account(SecretA, "Alpha"));
        laptop.Change(vault => vault.Accounts.Add(Account(SecretB, "Beta")));
        laptop.Sync();
        desktop.Sync();
        Assert.Equal(new[] { "Alpha", "Beta" }, desktop.Issuers());
    }

    [Fact]
    public void SyncingTwiceWritesNothingNew()
    {
        var (desktop, _) = TwoPcs(Account(SecretA, "Alpha"));
        desktop.Sync();
        var outcome = desktop.Sync();
        Assert.Null(outcome.Local);
        Assert.Null(outcome.Own);
        Assert.Equal(0, outcome.Unreadable);
    }

    [Fact]
    public void DeletionReachesTheOtherPcAndStaysDeleted()
    {
        var (desktop, laptop) = TwoPcs(Account(SecretA, "Alpha"), Account(SecretB, "Beta"));
        desktop.Change(vault => vault.Remove(vault.Accounts.First(account => account.Issuer == "Alpha"), 2000));
        desktop.Sync();
        laptop.Sync();
        desktop.Sync();
        Assert.Equal(new[] { "Beta" }, laptop.Issuers());
        Assert.Equal(new[] { "Beta" }, desktop.Issuers());
    }

    [Fact]
    public void DeletedSecretIsGoneFromSharedFiles()
    {
        var (desktop, laptop) = TwoPcs(Account(SecretA, "Alpha"));
        desktop.Change(vault => vault.Remove(vault.Accounts[0], 2000));
        desktop.Sync();
        laptop.Sync();

        using var key = desktop.Open();
        foreach (var file in SyncFolder.ReadAll(_folder))
        {
            var bytes = key.CopyKey();
            using var opened = Vault.OpenWithKey(file.Content, bytes);
            SecretBuffer.Wipe(bytes);
            Assert.Empty(opened.Accounts);
        }
    }

    [Fact]
    public void AddingAgainAfterDeletionWins()
    {
        var (desktop, laptop) = TwoPcs(Account(SecretA, "Alpha"));
        desktop.Change(vault => vault.Remove(vault.Accounts[0], 2000));
        desktop.Sync();
        laptop.Sync();
        laptop.Change(vault => vault.Accounts.Add(Account(SecretA, "Alpha again", 3000)));
        laptop.Sync();
        desktop.Sync();
        Assert.Equal(new[] { "Alpha again" }, desktop.Issuers());
    }

    [Fact]
    public void LatestRenameWinsOnBothPcs()
    {
        var (desktop, laptop) = TwoPcs(Account(SecretA, "Alpha"));
        desktop.Change(vault => { vault.Accounts[0].Issuer = "Older"; vault.Accounts[0].ModifiedAt = 2000; });
        laptop.Change(vault => { vault.Accounts[0].Issuer = "Newer"; vault.Accounts[0].ModifiedAt = 3000; });
        desktop.Sync();
        laptop.Sync();
        desktop.Sync();
        Assert.Equal(new[] { "Newer" }, desktop.Issuers());
        Assert.Equal(new[] { "Newer" }, laptop.Issuers());
    }

    [Fact]
    public void SameAccountAddedOnBothPcsAppearsOnce()
    {
        var (desktop, laptop) = TwoPcs();
        desktop.Change(vault => vault.Accounts.Add(Account(SecretA, "Alpha", 2000)));
        laptop.Change(vault => vault.Accounts.Add(Account(SecretA, "Alpha", 2500)));
        desktop.Sync();
        laptop.Sync();
        desktop.Sync();
        Assert.Single(desktop.Issuers());
        Assert.Single(laptop.Issuers());
    }

    [Fact]
    public void OldCopyOfAFileCannotUndoChanges()
    {
        var (desktop, laptop) = TwoPcs(Account(SecretA, "Alpha"));
        var path = Path.Combine(_folder, "desktop" + SyncFolder.Extension);
        var old = File.ReadAllBytes(path);

        desktop.Change(vault => vault.Remove(vault.Accounts[0], 2000));
        desktop.Sync();
        laptop.Sync();
        File.WriteAllBytes(path, old);
        laptop.Sync();
        Assert.Empty(laptop.Issuers());
    }

    [Fact]
    public void FileFromADifferentVaultIsCountedAndIgnored()
    {
        var (desktop, _) = TwoPcs(Account(SecretA, "Alpha"));
        using (var stranger = Vault.Create(Signature(9), new byte[20]))
        {
            stranger.Accounts.Add(Account(SecretB, "Stranger"));
            SyncFolder.Write(_folder, "stranger" + SyncFolder.Extension, stranger.ToSyncBytes());
        }

        Assert.Equal(1, desktop.Sync().Unreadable);
        Assert.Equal(new[] { "Alpha" }, desktop.Issuers());
    }

    [Fact]
    public void AlteredFileIsCountedAndIgnored()
    {
        var (desktop, laptop) = TwoPcs(Account(SecretA, "Alpha"));
        var path = Path.Combine(_folder, "laptop" + SyncFolder.Extension);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);

        Assert.Equal(1, desktop.Sync().Unreadable);
        Assert.Equal(new[] { "Alpha" }, desktop.Issuers());
        Assert.Equal(new[] { "Alpha" }, laptop.Issuers());
    }

    [Fact]
    public void AChangeSavedDuringSyncIsKept()
    {
        var (desktop, _) = TwoPcs(Account(SecretA, "Alpha"));

        // This copy was opened before the save below, as when a folder read takes a while.
        using var stale = desktop.Open();
        desktop.Change(vault => vault.Accounts.Add(Account(SecretB, "Beta", 2000)));

        var outcome = VaultSync.Apply(stale, desktop.Local, SyncFolder.ReadAll(_folder), "desktop" + SyncFolder.Extension);
        Assert.Contains(stale.Accounts, account => account.Issuer == "Beta");
        Assert.NotNull(outcome.Own);
    }

    [Fact]
    public void FirstVersionVaultStillOpensAndUpgrades()
    {
        var signature = Signature(1);
        var file = BuildFirstVersionFile(signature, SecretA, "Legacy");

        byte[] upgraded;
        using (var vault = Vault.OpenWithHello(file, signature))
        {
            var account = Assert.Single(vault.Accounts);
            Assert.Equal("Legacy", account.Issuer);
            Assert.Equal(SecretA, account.Secret);
            Assert.Equal(0, account.ModifiedAt);
            vault.Remove(vault.Accounts[0], 500);
            vault.Accounts.Add(Account(SecretB, "New", 700));
            upgraded = vault.ToFileBytes();
        }

        Assert.Equal(2, upgraded[4]);
        using (var vault = Vault.OpenWithHello(upgraded, signature))
        {
            Assert.Equal("New", Assert.Single(vault.Accounts).Issuer);
            Assert.Equal(700, vault.Accounts[0].ModifiedAt);
            Assert.Equal(500, Assert.Single(vault.Tombstones).DeletedAt);
        }
        using (var vault = Vault.OpenWithRecovery(upgraded, Recovery))
            Assert.Single(vault.Accounts);
    }

    [Fact]
    public void ReaderIgnoresOtherFiles()
    {
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not a vault");
        File.WriteAllBytes(Path.Combine(_folder, "leftover" + SyncFolder.Extension + ".tmp"), new byte[8]);
        Assert.Empty(SyncFolder.ReadAll(_folder));
    }

    [Fact]
    public void FileNamesAreSafeAndUnique()
    {
        var first = SyncFolder.NewFileName("MY PC/1");
        Assert.StartsWith("MY-PC-1-", first);
        Assert.EndsWith(SyncFolder.Extension, first);
        Assert.NotEqual(first, SyncFolder.NewFileName("MY PC/1"));
    }

    /// <summary>Writes the original file format by hand, independent of the current writer.</summary>
    private static byte[] BuildFirstVersionFile(byte[] signature, byte[] secret, string issuer)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(32);
        var header = new byte[5 + 32 + 60 + 60];
        "HOTP"u8.CopyTo(header);
        header[4] = 1;
        salt.CopyTo(header, 5);
        Wrap(key, signature, salt, "HelloOtp hello wrap v1").CopyTo(header, 37);
        Wrap(key, Recovery, salt, "HelloOtp recovery wrap v1").CopyTo(header, 97);

        using var body = new MemoryStream();
        void Int(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            body.Write(bytes);
        }
        void Block(byte[] data)
        {
            Int(data.Length);
            body.Write(data);
        }

        Int(1);
        Block(Encoding.UTF8.GetBytes(issuer));
        Block(Encoding.UTF8.GetBytes("me"));
        Block(secret);
        body.WriteByte((byte)OtpAlgorithm.Sha1);
        body.WriteByte(6);
        Int(30);

        var plain = body.ToArray();
        var file = new byte[header.Length + 28 + plain.Length];
        header.CopyTo(file, 0);
        var nonce = RandomNumberGenerator.GetBytes(12);
        nonce.CopyTo(file, header.Length);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, file.AsSpan(header.Length + 28), file.AsSpan(header.Length + 12, 16), header);
        return file;
    }

    private static byte[] Wrap(byte[] key, byte[] material, byte[] salt, string info)
    {
        var kek = HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 32, salt, Encoding.ASCII.GetBytes(info));
        var aad = new byte[37];
        "HOTP"u8.CopyTo(aad);
        aad[4] = 1;
        salt.CopyTo(aad, 5);

        var wrap = new byte[60];
        RandomNumberGenerator.Fill(wrap.AsSpan(0, 12));
        using var aes = new AesGcm(kek, 16);
        aes.Encrypt(wrap.AsSpan(0, 12), key, wrap.AsSpan(28), wrap.AsSpan(12, 16), aad);
        return wrap;
    }
}
