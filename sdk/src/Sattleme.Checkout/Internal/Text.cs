using System;
using System.Globalization;
using System.Text;

namespace Sattleme.Checkout.Internal;

/// <summary>
/// Text rules every implementation applies alike (SPEC.md §6): lengths in Unicode code points,
/// case folding and trimming of ASCII only, strict UTF-8, lone surrogates refused. .NET's own
/// <c>Length</c>, <c>Trim()</c> and <c>ToLower*()</c> follow other rules (UTF-16 units, Unicode
/// spaces, the Kelvin sign lowered to "k").
/// </summary>
internal static class Text
{
    /// <summary>UTF-8 that throws on invalid bytes, never writes U+FFFD, and keeps a BOM as U+FEFF.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Unicode code points of <paramref name="text"/> (a lone surrogate counts as one).</summary>
    public static int CodePointLength(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            count++;
        }
        return count;
    }

    /// <summary>Bytes of <paramref name="text"/> in UTF-8 (a lone surrogate as U+FFFD, 3 bytes).</summary>
    public static int Utf8Length(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>The UTF-8 bytes of <paramref name="text"/>, which must be well-formed.</summary>
    public static byte[] Utf8(string text) => StrictUtf8.GetBytes(text);

    /// <summary>
    /// The text of UTF-8 <paramref name="bytes"/>; null when they are not valid UTF-8 (never
    /// repaired). A leading byte order mark is kept, as U+FEFF: only HTTP bodies drop one.
    /// </summary>
    public static string? DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary><paramref name="text"/> with A–Z lowered to a–z, and nothing else changed.</summary>
    public static string AsciiLower(string text)
    {
        var index = text.AsSpan().IndexOfAnyInRange('A', 'Z');
        if (index < 0)
        {
            return text;
        }
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });
    }

    /// <summary>Whether <paramref name="c"/> is ASCII whitespace: space, tab, line feed, carriage return.</summary>
    public static bool IsAsciiWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r';

    /// <summary><paramref name="text"/> without leading and trailing ASCII whitespace.</summary>
    public static string TrimAscii(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsAsciiWhitespace(text[start]))
        {
            start++;
        }
        while (end > start && IsAsciiWhitespace(text[end - 1]))
        {
            end--;
        }
        return text[start..end];
    }

    /// <summary><paramref name="text"/> without leading and trailing spaces (U+0020): a note's rule.</summary>
    public static string TrimSpaces(string text) => text.Trim(' ');

    /// <summary>Whether <paramref name="text"/> holds a C0 control character (U+0000 to U+001F) or DEL.</summary>
    public static bool HasControlCharacter(string text)
    {
        foreach (var c in text)
        {
            if (c < 0x20 || c == 0x7f)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether <paramref name="text"/> is well-formed Unicode: no lone surrogate.</summary>
    public static bool IsWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return false;
                }
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether <paramref name="value"/> is lowercase hex of exactly <paramref name="bytes"/> bytes.</summary>
    public static bool IsLowerHex(string? value, int bytes)
    {
        if (value is null || value.Length != bytes * 2)
        {
            return false;
        }
        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Lowercase hex of 32 bytes.</summary>
    public static bool IsHex32(string? value) => IsLowerHex(value, 32);

    /// <summary>Lowercase hex of 64 bytes.</summary>
    public static bool IsHex64(string? value) => IsLowerHex(value, 64);

    /// <summary>Whether <paramref name="value"/> is hex (any case) of exactly <paramref name="bytes"/> bytes.</summary>
    public static bool IsHexAnyCase(string? value, int bytes)
    {
        if (value is null || value.Length != bytes * 2)
        {
            return false;
        }
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Lowercase hex of <paramref name="bytes"/>.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        const string Digits = "0123456789abcdef";
        return string.Create(bytes.Length * 2, bytes.ToArray(), static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[2 * i] = Digits[source[i] >> 4];
                span[(2 * i) + 1] = Digits[source[i] & 0xf];
            }
        });
    }

    /// <summary>The bytes of hex <paramref name="hex"/> (any case); throws on anything else.</summary>
    public static byte[] FromHex(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            throw new FormatException("Odd hex length");
        }
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(2 * i, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }
        return bytes;
    }

    /// <summary>Whether every character of <paramref name="text"/> is a digit 0–9 (and there is one).</summary>
    public static bool IsDigits(string text, int min, int max)
    {
        if (text.Length < min || text.Length > max)
        {
            return false;
        }
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }
}
