using System.Text;
using HelloOtp.Core;
using Xunit;

namespace HelloOtp.Tests;

public class VaultTests
{
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes(new string('s', 256));
    private static readonly byte[] Recovery = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("12345678901234567890");

    private static byte[] SampleFile()
    {
        using var vault = Vault.Create(Signature, Recovery);
        vault.Accounts.Add(new OtpAccount("Exämple", "alice", SecretBuffer.CopyOf(Secret), OtpAlgorithm.Sha256, 8, 60));
        vault.Accounts.Add(new OtpAccount("Site", "bob", SecretBuffer.CopyOf(Secret)));
        return vault.ToFileBytes();
    }

    [Fact]
    public void RoundTripsWithHello()
    {
        using var vault = Vault.OpenWithHello(SampleFile(), Signature);
        Assert.Equal(2, vault.Accounts.Count);
        var first = vault.Accounts[0];
        Assert.Equal("Exämple", first.Issuer);
        Assert.Equal("alice", first.Label);
        Assert.Equal(Secret, first.Secret);
        Assert.Equal(OtpAlgorithm.Sha256, first.Algorithm);
        Assert.Equal(8, first.Digits);
        Assert.Equal(60, first.Period);
    }

    [Fact]
    public void RecoveryCodeOpensVault()
    {
        using var vault = Vault.OpenWithRecovery(SampleFile(), Recovery);
        Assert.Equal(2, vault.Accounts.Count);
    }

    [Fact]
    public void WrongSignatureFails() =>
        Assert.Throws<VaultException>(() => Vault.OpenWithHello(SampleFile(), new byte[256]));

    [Fact]
    public void WrongRecoveryCodeFails() =>
        Assert.Throws<VaultException>(() => Vault.OpenWithRecovery(SampleFile(), new byte[20]));

    [Fact]
    public void SignatureDoesNotOpenRecoverySlot() =>
        Assert.Throws<VaultException>(() => Vault.OpenWithRecovery(SampleFile(), Signature));

    [Fact]
    public void AnyFlippedByteIsRejected()
    {
        var file = SampleFile();
        for (var i = 0; i < file.Length; i++)
        {
            var damaged = (byte[])file.Clone();
            damaged[i] ^= 0x40;

            // The recovery wrap is unused when opening with Hello, but the body authenticates it.
            Assert.Throws<VaultException>(() => Vault.OpenWithHello(damaged, Signature));
        }
    }

    [Fact]
    public void TruncatedFileIsRejected() =>
        Assert.Throws<VaultException>(() => Vault.OpenWithHello(SampleFile()[..50], Signature));

    [Fact]
    public void RewrapBindsNewHelloKeyAndKeepsRecovery()
    {
        var replacement = new byte[256];
        Array.Fill(replacement, (byte)7);

        byte[] rewrapped;
        using (var vault = Vault.OpenWithRecovery(SampleFile(), Recovery))
        {
            vault.RewrapHello(replacement);
            rewrapped = vault.ToFileBytes();
        }

        using (var vault = Vault.OpenWithHello(rewrapped, replacement))
            Assert.Equal(2, vault.Accounts.Count);
        using (var vault = Vault.OpenWithRecovery(rewrapped, Recovery))
            Assert.Equal(2, vault.Accounts.Count);
        Assert.Throws<VaultException>(() => Vault.OpenWithHello(rewrapped, Signature));
    }

    [Fact]
    public void EachSaveUsesAFreshNonce()
    {
        using var vault = Vault.Create(Signature, Recovery);
        Assert.NotEqual(vault.ToFileBytes(), vault.ToFileBytes());
    }

    [Fact]
    public void ForgetKeyBlocksSavingButKeepsAccounts()
    {
        using var vault = Vault.OpenWithHello(SampleFile(), Signature);
        vault.ForgetKey();
        Assert.Equal("287082", vault.Accounts[1].CodeAt(59));
        Assert.Throws<InvalidOperationException>(() => vault.ToFileBytes());
    }

    [Fact]
    public void DisposeWipesSecrets()
    {
        var vault = Vault.OpenWithHello(SampleFile(), Signature);
        var secret = vault.Accounts[0].Secret;
        vault.Dispose();
        Assert.All(secret, b => Assert.Equal(0, b));
    }

    [Fact]
    public void FileContainsNoPlaintext()
    {
        var text = Encoding.Latin1.GetString(SampleFile());
        Assert.DoesNotContain("alice", text);
        Assert.DoesNotContain("12345678901234567890", text);
    }

    [Fact]
    public void StoreKeepsBackupOfPreviousVersion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HelloOtpTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new VaultStore(directory);
            Assert.False(store.Exists);
            store.Write(new byte[] { 1 });
            Assert.True(store.Exists);
            store.Write(new byte[] { 2 });
            Assert.Equal(new byte[] { 2 }, store.Read());
            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(directory, "vault.bak")));
            Assert.False(File.Exists(Path.Combine(directory, "vault.bin.tmp")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
