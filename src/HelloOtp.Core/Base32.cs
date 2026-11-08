using System.Text;

namespace HelloOtp.Core;

/// <summary>RFC 4648 base32, tolerant of the spaces, hyphens, padding and lowercase sites often use.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Decodes into a pinned buffer the caller should wipe.</summary>
    public static byte[] Decode(string text)
    {
        var symbols = 0;
        foreach (var c in text)
        {
            if (!IsSeparator(c))
                symbols++;
        }
        if (symbols == 0)
            throw new FormatException("The secret is empty.");

        var output = SecretBuffer.Allocate(symbols * 5 / 8);
        var index = 0;
        var buffer = 0;
        var bits = 0;
        foreach (var c in text)
        {
            if (IsSeparator(c))
                continue;
            var value = ValueOf(c);
            if (value < 0)
            {
                SecretBuffer.Wipe(output);
                throw new FormatException("The secret contains a character that is not valid base32.");
            }
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output[index++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }
        if (output.Length == 0)
            throw new FormatException("The secret is too short.");
        return output;
    }

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                text.Append(Alphabet[(buffer >> bits) & 31]);
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0)
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    private static bool IsSeparator(char c) => c is ' ' or '-' or '=' or '\t' or '\r' or '\n';

    private static int ValueOf(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a',
        >= '2' and <= '7' => c - '2' + 26,
        _ => -1,
    };
}
