using System;
using System.Text;

namespace Sattleme.Checkout.Internal;

/// <summary>
/// Base64 (RFC 4648) as the merchant's app writes it (SPEC.md §2): decoding is strict, one value
/// has one spelling, anything else is refused (null). <see cref="Convert.FromBase64String"/> skips
/// whitespace and is no base64url decoder: a damaged key would be read as another one.
/// </summary>
internal static class Base64
{
    /// <summary>RFC 4648 §4.</summary>
    private const string Standard = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    /// <summary>RFC 4648 §5.</summary>
    private const string UrlSafe = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    /// <summary>Base64url without padding (RFC 4648 §5).</summary>
    public static string ToBase64Url(ReadOnlySpan<byte> bytes) => Encode(bytes, UrlSafe);

    /// <summary>The bytes of unpadded base64url text, or null when it is not canonical.</summary>
    public static byte[]? FromBase64Url(string text) => Decode(text, UrlSafe);

    /// <summary>Standard base64 with "=" padding (RFC 4648 §4), as NIP-44 payloads use it.</summary>
    public static string ToBase64(ReadOnlySpan<byte> bytes)
    {
        var text = Encode(bytes, Standard);
        return text + new string('=', (4 - (text.Length % 4)) % 4);
    }

    /// <summary>The bytes of padded standard base64 text, or null when it is not canonical.</summary>
    public static byte[]? FromBase64(string text)
    {
        if (text.Length % 4 != 0)
        {
            return null;
        }
        var unpadded = text;
        if (unpadded.EndsWith("==", StringComparison.Ordinal))
        {
            unpadded = unpadded[..^2];
        }
        else if (unpadded.EndsWith('='))
        {
            unpadded = unpadded[..^1];
        }
        return unpadded.Contains('=', StringComparison.Ordinal) ? null : Decode(unpadded, Standard);
    }

    private static string Encode(ReadOnlySpan<byte> bytes, string alphabet)
    {
        var text = new StringBuilder(((bytes.Length + 2) / 3) * 4);
        for (var i = 0; i < bytes.Length; i += 3)
        {
            var chunk = (bytes[i] << 16)
                | ((i + 1 < bytes.Length ? bytes[i + 1] : 0) << 8)
                | (i + 2 < bytes.Length ? bytes[i + 2] : 0);
            var chars = Math.Min(4, (((bytes.Length - i) * 8) + 5) / 6);
            for (var c = 0; c < chars; c++)
            {
                text.Append(alphabet[(chunk >> (18 - (c * 6))) & 63]);
            }
        }
        return text.ToString();
    }

    /// <summary>Unpadded text of <paramref name="alphabet"/> as bytes; null unless canonical.</summary>
    private static byte[]? Decode(string text, string alphabet)
    {
        if (text.Length % 4 == 1)
        {
            return null;
        }
        var bytes = new byte[text.Length * 6 / 8];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var c in text)
        {
            var value = alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                return null;
            }
            buffer = (buffer << 6) | value;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[index++] = (byte)((buffer >> bits) & 0xff);
            }
            buffer &= (1 << bits) - 1;
        }
        // Left-over bits are padding: zero in the canonical encoding.
        return buffer == 0 ? bytes : null;
    }
}
