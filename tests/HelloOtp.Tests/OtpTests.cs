using System.Text;
using HelloOtp.Core;
using Xunit;

namespace HelloOtp.Tests;

public class OtpTests
{
    private static readonly byte[] Seed20 = Encoding.ASCII.GetBytes("12345678901234567890");
    private static readonly byte[] Seed32 = Encoding.ASCII.GetBytes("12345678901234567890123456789012");
    private static readonly byte[] Seed64 = Encoding.ASCII.GetBytes("1234567890123456789012345678901234567890123456789012345678901234");

    [Theory]
    [InlineData(0, "755224")]
    [InlineData(1, "287082")]
    [InlineData(2, "359152")]
    [InlineData(3, "969429")]
    [InlineData(4, "338314")]
    [InlineData(5, "254676")]
    [InlineData(6, "287922")]
    [InlineData(7, "162583")]
    [InlineData(8, "399871")]
    [InlineData(9, "520489")]
    public void Rfc4226Vectors(ulong counter, string expected) =>
        Assert.Equal(expected, Hotp.Compute(Seed20, counter));

    [Theory]
    [InlineData(59L, "94287082", "46119246", "90693936")]
    [InlineData(1111111109L, "07081804", "68084774", "25091201")]
    [InlineData(1111111111L, "14050471", "67062674", "99943326")]
    [InlineData(1234567890L, "89005924", "91819424", "93441116")]
    [InlineData(2000000000L, "69279037", "90698825", "38618901")]
    [InlineData(20000000000L, "65353130", "77737706", "47863826")]
    public void Rfc6238Vectors(long time, string sha1, string sha256, string sha512)
    {
        Assert.Equal(sha1, Totp.Compute(Seed20, time, 30, OtpAlgorithm.Sha1, 8));
        Assert.Equal(sha256, Totp.Compute(Seed32, time, 30, OtpAlgorithm.Sha256, 8));
        Assert.Equal(sha512, Totp.Compute(Seed64, time, 30, OtpAlgorithm.Sha512, 8));
    }

    [Theory]
    [InlineData(0L, 30)]
    [InlineData(1L, 29)]
    [InlineData(29L, 1)]
    [InlineData(30L, 30)]
    public void SecondsRemaining(long time, int expected) =>
        Assert.Equal(expected, Totp.SecondsRemaining(time, 30));

    [Theory]
    [InlineData("MY======", "f")]
    [InlineData("MZXQ====", "fo")]
    [InlineData("MZXW6===", "foo")]
    [InlineData("MZXW6YQ=", "foob")]
    [InlineData("MZXW6YTB", "fooba")]
    [InlineData("MZXW6YTBOI======", "foobar")]
    [InlineData("mzxw 6ytb-oi", "foobar")]
    public void Base32Decodes(string text, string expected) =>
        Assert.Equal(expected, Encoding.ASCII.GetString(Base32.Decode(text)));

    [Theory]
    [InlineData("MZ1W")]
    [InlineData("MZ!W")]
    [InlineData("   ")]
    [InlineData("")]
    public void Base32RejectsBadInput(string text) =>
        Assert.Throws<FormatException>(() => Base32.Decode(text));

    [Fact]
    public void Base32RoundTrips()
    {
        var data = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        Assert.Equal(data, Base32.Decode(Base32.Encode(data)));
    }

    [Fact]
    public void OtpAuthFullLink()
    {
        using var account = OtpAuthUri.Parse("otpauth://totp/Example%20Co:alice%40example.com?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Example%20Co&algorithm=SHA256&digits=8&period=60");
        Assert.Equal("Example Co", account.Issuer);
        Assert.Equal("alice@example.com", account.Label);
        Assert.Equal(Seed20, account.Secret);
        Assert.Equal(OtpAlgorithm.Sha256, account.Algorithm);
        Assert.Equal(8, account.Digits);
        Assert.Equal(60, account.Period);
    }

    [Fact]
    public void OtpAuthDefaults()
    {
        using var account = OtpAuthUri.Parse("otpauth://totp/Site:bob?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
        Assert.Equal("Site", account.Issuer);
        Assert.Equal("bob", account.Label);
        Assert.Equal(OtpAlgorithm.Sha1, account.Algorithm);
        Assert.Equal(6, account.Digits);
        Assert.Equal(30, account.Period);
    }

    [Theory]
    [InlineData("otpauth://hotp/Site:bob?secret=GEZDGNBV&counter=1")]
    [InlineData("otpauth://totp/Site:bob?issuer=Site")]
    [InlineData("otpauth://totp/Site:bob?secret=GEZDGNBV&algorithm=MD5")]
    [InlineData("otpauth://totp/Site:bob?secret=GEZDGNBV&digits=4")]
    [InlineData("https://example.com/")]
    public void OtpAuthRejects(string link) =>
        Assert.Throws<FormatException>(() => OtpAuthUri.Parse(link));

    [Fact]
    public void RecoveryCodeRoundTrips()
    {
        var code = RecoveryCode.Generate();
        var text = RecoveryCode.Format(code);
        Assert.Equal(39, text.Length);
        Assert.Equal(code, RecoveryCode.Parse(text.ToLowerInvariant()));
    }

    [Fact]
    public void RecoveryCodeRejectsWrongLength() =>
        Assert.Throws<FormatException>(() => RecoveryCode.Parse("ABCD-EFGH"));
}
