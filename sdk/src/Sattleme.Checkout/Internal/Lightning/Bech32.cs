using System;
using System.Collections.Generic;

namespace Sattleme.Checkout.Internal.Lightning;

/// <summary>
/// Bech32 decoding (BIP-173), without the 90-character limit: BOLT11 invoices are longer. Bech32m
/// (BIP-350), for taproot addresses, differs by its checksum's constant alone. Case is ASCII's
/// only (SPEC.md §6): all lowercase or all uppercase, never mixed, nothing around it.
/// </summary>
internal static class Bech32
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private static readonly uint[] Generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];

    /// <summary>The checksum's constant: BIP-173's bech32.</summary>
    public const uint Bech32Constant = 1;

    /// <summary>The checksum's constant: BIP-350's bech32m.</summary>
    public const uint Bech32mConstant = 0x2bc830a3;

    /// <summary>
    /// The human-readable part (lowercase) and the 5-bit words (checksum removed) of a bech32 string,
    /// or null when it is not valid bech32 (or bech32m, with <paramref name="constant"/>).
    /// </summary>
    public static (string Hrp, int[] Words)? Decode(string input, uint constant = Bech32Constant)
    {
        var hasLower = false;
        var hasUpper = false;
        foreach (var c in input)
        {
            // Only ASCII: anything else is no bech32 (never lowered by Unicode's rules).
            if (c is < (char)33 or > (char)126)
            {
                return null;
            }
            hasLower |= c is >= 'a' and <= 'z';
            hasUpper |= c is >= 'A' and <= 'Z';
        }
        if (hasLower && hasUpper)
        {
            return null;
        }
        var text = Text.AsciiLower(input);
        var separator = text.LastIndexOf('1');
        if (separator < 1 || separator + 7 > text.Length)
        {
            return null;
        }
        var hrp = text[..separator];
        var values = new int[text.Length - separator - 1];
        for (var i = 0; i < values.Length; i++)
        {
            var value = Charset.IndexOf(text[separator + 1 + i], StringComparison.Ordinal);
            if (value < 0)
            {
                return null;
            }
            values[i] = value;
        }
        var all = new List<int>(ExpandHrp(hrp));
        all.AddRange(values);
        if (Polymod(all) != constant)
        {
            return null;
        }
        return (hrp, values[..^6]);
    }

    /// <summary>Regroups 5-bit words into bytes; the trailing padding bits (fewer than 8) are dropped.</summary>
    public static byte[] WordsToBytes(ReadOnlySpan<int> words)
    {
        var bytes = new List<byte>(words.Length * 5 / 8);
        var accumulator = 0;
        var bits = 0;
        foreach (var word in words)
        {
            accumulator = ((accumulator << 5) | word) & 0xfff;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((accumulator >> bits) & 0xff));
            }
        }
        return [.. bytes];
    }

    /// <summary>The big-endian unsigned integer of 5-bit words (at most 12 of them: 60 bits).</summary>
    public static long WordsToInteger(ReadOnlySpan<int> words)
    {
        long value = 0;
        foreach (var word in words)
        {
            value = (value * 32) + word;
        }
        return value;
    }

    private static uint Polymod(IEnumerable<int> values)
    {
        uint chk = 1;
        foreach (var value in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ (uint)value;
            for (var i = 0; i < 5; i++)
            {
                if (((top >> i) & 1) != 0)
                {
                    chk ^= Generator[i];
                }
            }
        }
        return chk;
    }

    private static List<int> ExpandHrp(string hrp)
    {
        var expanded = new List<int>((hrp.Length * 2) + 1);
        foreach (var c in hrp)
        {
            expanded.Add(c >> 5);
        }
        expanded.Add(0);
        foreach (var c in hrp)
        {
            expanded.Add(c & 31);
        }
        return expanded;
    }
}
