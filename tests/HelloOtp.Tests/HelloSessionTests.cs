using System.Text;
using HelloOtp.Core;
using Xunit;

namespace HelloOtp.Tests;

public class HelloSessionTests
{
    private const long TwelveHours = 12L * 60 * 60 * 1000;

    private static byte[] Key() => SecretBuffer.CopyOf(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public void StartsEmpty() => Assert.Null(new HelloSession(() => 0).TryCopyKey());

    [Fact]
    public void ReturnsTheKeyRepeatedly()
    {
        var session = new HelloSession(() => 0);
        session.Begin(Key());
        Assert.Equal(Key(), session.TryCopyKey());
        Assert.Equal(Key(), session.TryCopyKey());
    }

    [Fact]
    public void HoldsTheKeyEncrypted()
    {
        var held = Key();
        var session = new HelloSession(() => 0);
        session.Begin(held);
        Assert.NotEqual(Key(), held);
    }

    [Fact]
    public void EndForgetsTheKey()
    {
        var held = Key();
        var session = new HelloSession(() => 0);
        session.Begin(held);
        session.End();
        Assert.Null(session.TryCopyKey());
        Assert.False(session.IsActive);
        Assert.All(held, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ExpiresAtTwelveHours()
    {
        long now = 1000;
        var session = new HelloSession(() => now);
        session.Begin(Key());

        now = 1000 + TwelveHours - 1;
        Assert.NotNull(session.TryCopyKey());

        now = 1000 + TwelveHours;
        Assert.Null(session.TryCopyKey());
        Assert.False(session.IsActive);
    }

    [Fact]
    public void UsingTheSessionDoesNotExtendIt()
    {
        long now = 0;
        var session = new HelloSession(() => now);
        session.Begin(Key());

        now = TwelveHours - 1000;
        Assert.NotNull(session.TryCopyKey());

        now = TwelveHours + 1;
        Assert.Null(session.TryCopyKey());
    }

    [Fact]
    public void ANewCheckRestartsTheClock()
    {
        long now = 0;
        var session = new HelloSession(() => now);
        session.Begin(Key());

        now = TwelveHours - 1;
        session.Begin(Key());

        now = TwelveHours + 1000;
        Assert.Equal(Key(), session.TryCopyKey());
    }

    [Fact]
    public void RefusesKeysItCannotProtect()
    {
        var odd = new byte[15];
        var session = new HelloSession(() => 0);
        session.Begin(odd);
        Assert.False(session.IsActive);
    }

    [Fact]
    public void VaultOpensWithRememberedKey()
    {
        var signature = new byte[256];
        var recovery = new byte[20];
        byte[] file;
        byte[] key;
        using (var vault = Vault.Create(signature, recovery))
        {
            vault.Accounts.Add(new OtpAccount("Site", "bob", SecretBuffer.CopyOf(Encoding.ASCII.GetBytes("12345678901234567890"))));
            file = vault.ToFileBytes();
            key = vault.CopyKey();
        }

        using var opened = Vault.OpenWithKey(file, key);
        Assert.Equal("287082", opened.Accounts[0].CodeAt(59));
        Assert.Throws<VaultException>(() => Vault.OpenWithKey(file, new byte[32]));

        var damaged = (byte[])file.Clone();
        damaged[^1] ^= 1;
        Assert.Throws<VaultException>(() => Vault.OpenWithKey(damaged, key));
    }

    [Fact]
    public void CopyKeyFailsAfterForgetKey()
    {
        using var vault = Vault.Create(new byte[256], new byte[20]);
        vault.ForgetKey();
        Assert.Throws<InvalidOperationException>(() => vault.CopyKey());
    }
}
