using System.Text;

namespace HelloOtp.Core;

public sealed class MigrationBatch
{
    public List<OtpAccount> Accounts { get; } = new();
    public List<string> Skipped { get; } = new();
    public int BatchIndex { get; set; }
    public int BatchSize { get; set; } = 1;
    public int BatchId { get; set; }
}

/// <summary>Decodes Google Authenticator's otpauth-migration:// export, a base64 protobuf message.</summary>
public static class MigrationPayload
{
    private const string Prefix = "otpauth-migration://";

    private const int TypeHotp = 1;
    private const int AlgorithmMd5 = 4;

    public static bool IsMatch(string text) => text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static MigrationBatch Parse(string uri)
    {
        uri = uri.Trim();
        if (!IsMatch(uri))
            throw new FormatException("This is not an otpauth-migration:// link.");

        var data = ExtractData(uri);
        var bytes = DecodeBase64(data);
        try
        {
            return ReadPayload(bytes);
        }
        finally
        {
            SecretBuffer.Wipe(bytes);
        }
    }

    private static string ExtractData(string uri)
    {
        var question = uri.IndexOf('?');
        if (question >= 0)
        {
            foreach (var pair in uri[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (pair.StartsWith("data=", StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(pair[5..]);
            }
        }
        throw new FormatException("The otpauth-migration:// link has no data.");
    }

    private static byte[] DecodeBase64(string text)
    {
        var normalized = new StringBuilder(text.Length + 3);
        foreach (var c in text)
        {
            normalized.Append(c switch
            {
                ' ' => '+',
                '-' => '+',
                '_' => '/',
                _ => c,
            });
        }
        while (normalized.Length % 4 != 0)
            normalized.Append('=');
        try
        {
            var decoded = Convert.FromBase64String(normalized.ToString());
            var pinned = SecretBuffer.CopyOf(decoded);
            SecretBuffer.Wipe(decoded);
            return pinned;
        }
        catch (FormatException)
        {
            throw new FormatException("The otpauth-migration:// data is not valid base64.");
        }
    }

    private static MigrationBatch ReadPayload(ReadOnlySpan<byte> bytes)
    {
        var batch = new MigrationBatch();
        var reader = new ProtoReader(bytes);
        try
        {
            while (reader.TryReadTag(out var field, out var wire))
            {
                switch (field)
                {
                    case 1 when wire == ProtoReader.WireLength:
                        ReadParameters(reader.ReadBytes(), batch);
                        break;
                    case 3 when wire == ProtoReader.WireVarint:
                        batch.BatchSize = Math.Max(1, (int)reader.ReadVarint());
                        break;
                    case 4 when wire == ProtoReader.WireVarint:
                        batch.BatchIndex = (int)reader.ReadVarint();
                        break;
                    case 5 when wire == ProtoReader.WireVarint:
                        batch.BatchId = (int)reader.ReadVarint();
                        break;
                    default:
                        reader.Skip(wire);
                        break;
                }
            }
        }
        catch
        {
            foreach (var account in batch.Accounts)
                account.Dispose();
            throw;
        }
        return batch;
    }

    private static void ReadParameters(ReadOnlySpan<byte> bytes, MigrationBatch batch)
    {
        var reader = new ProtoReader(bytes);
        ReadOnlySpan<byte> secret = default;
        var name = "";
        var issuer = "";
        var algorithm = 0;
        var digits = 0;
        var type = 0;

        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == ProtoReader.WireLength:
                    secret = reader.ReadBytes();
                    break;
                case 2 when wire == ProtoReader.WireLength:
                    name = Encoding.UTF8.GetString(reader.ReadBytes());
                    break;
                case 3 when wire == ProtoReader.WireLength:
                    issuer = Encoding.UTF8.GetString(reader.ReadBytes());
                    break;
                case 4 when wire == ProtoReader.WireVarint:
                    algorithm = (int)reader.ReadVarint();
                    break;
                case 5 when wire == ProtoReader.WireVarint:
                    digits = (int)reader.ReadVarint();
                    break;
                case 6 when wire == ProtoReader.WireVarint:
                    type = (int)reader.ReadVarint();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        var names = OtpAccount.NormalizeNames(issuer, name);
        var title = names.Issuer.Length > 0 ? $"{names.Issuer} ({names.Label})" : names.Label;
        if (type == TypeHotp)
        {
            batch.Skipped.Add($"{title}: counter-based (HOTP) accounts are not supported.");
            return;
        }
        if (algorithm >= AlgorithmMd5)
        {
            batch.Skipped.Add($"{title}: its hash algorithm is not supported.");
            return;
        }
        if (secret.IsEmpty)
        {
            batch.Skipped.Add($"{title}: the export has no secret for it.");
            return;
        }

        batch.Accounts.Add(new OtpAccount(
            names.Issuer,
            names.Label,
            SecretBuffer.CopyOf(secret),
            algorithm switch { 2 => OtpAlgorithm.Sha256, 3 => OtpAlgorithm.Sha512, _ => OtpAlgorithm.Sha1 },
            digits == 2 ? 8 : 6));
    }

    /// <summary>Just enough of the protobuf wire format, bounds-checked at every read.</summary>
    private ref struct ProtoReader
    {
        public const int WireVarint = 0;
        public const int WireFixed64 = 1;
        public const int WireLength = 2;
        public const int WireFixed32 = 5;

        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public ProtoReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public bool TryReadTag(out int field, out int wire)
        {
            field = 0;
            wire = 0;
            if (_position >= _data.Length)
                return false;
            var tag = ReadVarint();
            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            return true;
        }

        public ulong ReadVarint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (_position >= _data.Length)
                    throw Malformed();
                var b = _data[_position++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return result;
            }
            throw Malformed();
        }

        public ReadOnlySpan<byte> ReadBytes()
        {
            var length = ReadVarint();
            if (length > (ulong)(_data.Length - _position))
                throw Malformed();
            var slice = _data.Slice(_position, (int)length);
            _position += (int)length;
            return slice;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case WireVarint:
                    ReadVarint();
                    break;
                case WireLength:
                    ReadBytes();
                    break;
                case WireFixed64:
                    Advance(8);
                    break;
                case WireFixed32:
                    Advance(4);
                    break;
                default:
                    throw Malformed();
            }
        }

        private void Advance(int count)
        {
            if (count > _data.Length - _position)
                throw Malformed();
            _position += count;
        }

        private static FormatException Malformed() => new("The Google Authenticator export is damaged or incomplete.");
    }
}
