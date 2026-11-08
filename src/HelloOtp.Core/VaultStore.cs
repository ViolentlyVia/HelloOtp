using System.Security.Cryptography;
using System.Text;

namespace HelloOtp.Core;

/// <summary>Reads and atomically replaces the vault file, keeping the previous version as vault.bak.</summary>
public sealed class VaultStore
{
    private readonly string _path;
    private readonly string _backupPath;

    public VaultStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "vault.bin");
        _backupPath = Path.Combine(directory, "vault.bak");
    }

    public bool Exists => File.Exists(_path);

    public byte[] Read() => File.ReadAllBytes(_path);

    public void Write(byte[] content)
    {
        var temporary = _path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_path))
            File.Replace(temporary, _path, _backupPath);
        else
            File.Move(temporary, _path);
    }
}

/// <summary>The 160-bit code that restores the vault if the Windows Hello key is lost.</summary>
public static class RecoveryCode
{
    public const int ByteLength = 20;

    public static byte[] Generate()
    {
        var code = SecretBuffer.Allocate(ByteLength);
        RandomNumberGenerator.Fill(code);
        return code;
    }

    public static string Format(ReadOnlySpan<byte> code)
    {
        var text = Base32.Encode(code);
        var grouped = new StringBuilder(text.Length + text.Length / 4);
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
                grouped.Append('-');
            grouped.Append(text[i]);
        }
        return grouped.ToString();
    }

    /// <summary>Accepts handwritten input, reading 0, 1 and 8 as the letters they resemble.</summary>
    public static byte[] Parse(string text)
    {
        var corrected = text.Replace('0', 'O').Replace('1', 'I').Replace('8', 'B');
        var code = Base32.Decode(corrected);
        if (code.Length != ByteLength)
        {
            SecretBuffer.Wipe(code);
            throw new FormatException("A recovery code has 32 characters.");
        }
        return code;
    }
}
