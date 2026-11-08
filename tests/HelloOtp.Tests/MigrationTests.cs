using System.Text;
using HelloOtp.Core;
using Xunit;
using ZXing;
using ZXing.QrCode;
using FormatException = System.FormatException;

namespace HelloOtp.Tests;

public class MigrationTests
{
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("12345678901234567890");

    private static void Varint(List<byte> output, ulong value)
    {
        while (value >= 0x80)
        {
            output.Add((byte)(value | 0x80));
            value >>= 7;
        }
        output.Add((byte)value);
    }

    private static void Field(List<byte> output, int field, ulong value)
    {
        Varint(output, (ulong)(field << 3));
        Varint(output, value);
    }

    private static void Field(List<byte> output, int field, byte[] value)
    {
        Varint(output, (ulong)((field << 3) | 2));
        Varint(output, (ulong)value.Length);
        output.AddRange(value);
    }

    private static byte[] Parameters(byte[] secret, string name, string issuer, int algorithm = 1, int digits = 1, int type = 2)
    {
        var output = new List<byte>();
        Field(output, 1, secret);
        Field(output, 2, Encoding.UTF8.GetBytes(name));
        Field(output, 3, Encoding.UTF8.GetBytes(issuer));
        Field(output, 4, (ulong)algorithm);
        Field(output, 5, (ulong)digits);
        Field(output, 6, (ulong)type);
        return output.ToArray();
    }

    private static string Link(byte[] payload) =>
        "otpauth-migration://offline?data=" + Uri.EscapeDataString(Convert.ToBase64String(payload));

    private static byte[] Payload(int index, int size, int id, params byte[][] parameters)
    {
        var output = new List<byte>();
        foreach (var entry in parameters)
            Field(output, 1, entry);
        Field(output, 2, 1);
        Field(output, 3, (ulong)size);
        Field(output, 4, (ulong)index);
        Field(output, 5, (ulong)id);
        return output.ToArray();
    }

    [Fact]
    public void DecodesAccountsAndComputesCodes()
    {
        var batch = MigrationPayload.Parse(Link(Payload(0, 1, 77,
            Parameters(Secret, "Example:alice", "Example"),
            Parameters(Secret, "bob@site", "Site", algorithm: 2, digits: 2))));

        Assert.Equal(2, batch.Accounts.Count);
        Assert.Equal("Example", batch.Accounts[0].Issuer);
        Assert.Equal("alice", batch.Accounts[0].Label);
        Assert.Equal(Secret, batch.Accounts[0].Secret);
        Assert.Equal("287082", batch.Accounts[0].CodeAt(59));
        Assert.Equal(OtpAlgorithm.Sha256, batch.Accounts[1].Algorithm);
        Assert.Equal(8, batch.Accounts[1].Digits);
        Assert.Equal(30, batch.Accounts[1].Period);
        Assert.Equal(77, batch.BatchId);
    }

    [Fact]
    public void AcceptsSpacesFromDecodedPlusSigns()
    {
        // A base64 "+" turned into a space or left unescaped must still decode.
        var payload = Payload(0, 1, 1, Parameters(new byte[] { 0xFB, 0xEF, 0xBE, 0xFB, 0xEF, 0xBE }, "a", "b"));
        var link = "otpauth-migration://offline?data=" + Convert.ToBase64String(payload);
        var batch = MigrationPayload.Parse(link);
        Assert.Single(batch.Accounts);
        Assert.Equal(new byte[] { 0xFB, 0xEF, 0xBE, 0xFB, 0xEF, 0xBE }, batch.Accounts[0].Secret);
    }

    [Fact]
    public void ReportsMultipleBatches()
    {
        var first = MigrationPayload.Parse(Link(Payload(0, 2, 9, Parameters(Secret, "one", "A"))));
        var second = MigrationPayload.Parse(Link(Payload(1, 2, 9, Parameters(Encoding.ASCII.GetBytes("abcdefghij"), "two", "B"))));
        Assert.Equal((0, 2, 9), (first.BatchIndex, first.BatchSize, first.BatchId));
        Assert.Equal((1, 2, 9), (second.BatchIndex, second.BatchSize, second.BatchId));

        var merged = new List<OtpAccount>();
        Assert.Equal(1, AccountMerge.Merge(merged, first.Accounts));
        Assert.Equal(1, AccountMerge.Merge(merged, second.Accounts));
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void SkipsHotpAndMd5WithWarnings()
    {
        var batch = MigrationPayload.Parse(Link(Payload(0, 1, 1,
            Parameters(Secret, "counter", "A", type: 1),
            Parameters(Secret, "old", "B", algorithm: 4),
            Parameters(Secret, "good", "C"))));
        Assert.Single(batch.Accounts);
        Assert.Equal("C", batch.Accounts[0].Issuer);
        Assert.Equal(2, batch.Skipped.Count);
        Assert.Contains("HOTP", batch.Skipped[0]);
    }

    [Fact]
    public void MergeDropsDuplicates()
    {
        var target = new List<OtpAccount> { new("A", "x", SecretBuffer.CopyOf(Secret)) };
        var duplicate = new OtpAccount("Renamed", "y", SecretBuffer.CopyOf(Secret));
        var added = AccountMerge.Merge(target, new[] { duplicate });
        Assert.Equal(0, added);
        Assert.Single(target);
        Assert.All(duplicate.Secret, b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("otpauth-migration://offline")]
    [InlineData("otpauth-migration://offline?data=@@@@")]
    public void RejectsBadLinks(string link) =>
        Assert.Throws<FormatException>(() => MigrationPayload.Parse(link));

    [Fact]
    public void RejectsTruncatedPayloads()
    {
        var payload = Payload(0, 1, 1, Parameters(Secret, "a", "b"));
        for (var length = 1; length < 12; length++)
        {
            var cut = payload[..length];
            Assert.Throws<FormatException>(() => MigrationPayload.Parse(Link(cut)));
        }
    }

    [Fact]
    public void ReadsQrCodeFromPixels()
    {
        var link = Link(Payload(0, 1, 5, Parameters(Secret, "qr", "Reader")));
        var matrix = new QRCodeWriter().encode(link, BarcodeFormat.QR_CODE, 500, 500);

        var pixels = new byte[matrix.Width * matrix.Height * 4];
        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                var shade = matrix[x, y] ? (byte)0 : (byte)255;
                var i = (y * matrix.Width + x) * 4;
                pixels[i] = shade;
                pixels[i + 1] = shade;
                pixels[i + 2] = shade;
                pixels[i + 3] = 255;
            }
        }

        var decoded = QrDecoder.DecodeBgra32(pixels, matrix.Width, matrix.Height);
        Assert.Equal(link, Assert.Single(decoded));
    }
}
