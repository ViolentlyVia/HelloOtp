using System.Globalization;

namespace HelloOtp.Core;

/// <summary>Parses single-account otpauth:// links.</summary>
public static class OtpAuthUri
{
    private const string Prefix = "otpauth://";

    public static bool IsMatch(string text) => text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static OtpAccount Parse(string text)
    {
        text = text.Trim();
        if (!IsMatch(text))
            throw new FormatException("This is not an otpauth:// link.");

        var rest = text[Prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0)
            throw new FormatException("The otpauth:// link has no account name.");
        var type = rest[..slash];
        if (type.Equals("hotp", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Counter-based (HOTP) accounts are not supported.");
        if (!type.Equals("totp", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Only time-based (TOTP) accounts are supported.");

        rest = rest[(slash + 1)..];
        var question = rest.IndexOf('?');
        if (question < 0)
            throw new FormatException("The otpauth:// link has no secret.");
        var name = Uri.UnescapeDataString(rest[..question]);

        string? secret = null;
        var issuer = "";
        var algorithm = OtpAlgorithm.Sha1;
        var digits = 6;
        var period = 30;
        foreach (var pair in rest[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0)
                continue;
            var value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            switch (pair[..equals].ToLowerInvariant())
            {
                case "secret":
                    secret = value;
                    break;
                case "issuer":
                    issuer = value;
                    break;
                case "algorithm":
                    algorithm = ParseAlgorithm(value);
                    break;
                case "digits":
                    digits = ParseNumber(value, "digits");
                    break;
                case "period":
                    period = ParseNumber(value, "period");
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(secret))
            throw new FormatException("The otpauth:// link has no secret.");

        var names = OtpAccount.NormalizeNames(issuer, name);
        var bytes = Base32.Decode(secret);
        try
        {
            return new OtpAccount(names.Issuer, names.Label, bytes, algorithm, digits, period);
        }
        catch
        {
            SecretBuffer.Wipe(bytes);
            throw;
        }
    }

    private static OtpAlgorithm ParseAlgorithm(string value) => value.ToUpperInvariant() switch
    {
        "SHA1" => OtpAlgorithm.Sha1,
        "SHA256" => OtpAlgorithm.Sha256,
        "SHA512" => OtpAlgorithm.Sha512,
        _ => throw new FormatException("The hash algorithm is not supported."),
    };

    private static int ParseNumber(string value, string name) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new FormatException($"The {name} value is not a number.");
}
